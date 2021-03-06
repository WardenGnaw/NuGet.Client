﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using NuGet.PackageManagement.UI;
using Task = System.Threading.Tasks.Task;

namespace NuGet.PackageManagement.VisualStudio
{
    /// <summary>
    /// Solution restore job scheduler.
    /// </summary>
    [Export(typeof(ISolutionRestoreWorker))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class SolutionRestoreWorker : SolutionEventsListener, ISolutionRestoreWorker, IDisposable
    {
        private const int IdleTimeoutMs = 400;
        private const int RequestQueueLimit = 150;
        private const int PromoteAttemptsLimit = 150;

        private readonly IServiceProvider _serviceProvider;
        private readonly AsyncLazy<ErrorListProvider> _errorListProvider;
        private EnvDTE.SolutionEvents _solutionEvents;
        private readonly IVsSolutionManager _solutionManager;
        private readonly Common.ILogger _logger;

        private CancellationTokenSource _workerCts;
        private Lazy<Task> _backgroundJobRunner;
        private Lazy<BlockingCollection<SolutionRestoreRequest>> _pendingRequests;
        private BackgroundRestoreOperation _pendingRestore;
        private Task<bool> _activeRestoreTask;

        private SolutionRestoreJobContext _restoreJobContext;

        private readonly JoinableTaskCollection _joinableCollection;
        private readonly JoinableTaskFactory _joinableFactory;

        private ErrorListProvider ErrorListProvider => ThreadHelper.JoinableTaskFactory.Run(_errorListProvider.GetValueAsync);

        public Task<bool> CurrentRestoreOperation => _activeRestoreTask;

        public bool IsBusy => !_activeRestoreTask.IsCompleted;

        [ImportingConstructor]
        public SolutionRestoreWorker(
            [Import(typeof(SVsServiceProvider))]
            IServiceProvider serviceProvider,
            IVsSolutionManager solutionManager,
            [Import(typeof(VisualStudioActivityLogger))]
            Common.ILogger logger)
        {
            if (serviceProvider == null)
            {
                throw new ArgumentNullException(nameof(serviceProvider));
            }

            if (solutionManager == null)
            {
                throw new ArgumentNullException(nameof(solutionManager));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            _serviceProvider = serviceProvider;
            _solutionManager = solutionManager;
            _logger = logger;

            var joinableTaskContextNode = new JoinableTaskContextNode(ThreadHelper.JoinableTaskContext);
            _joinableCollection = joinableTaskContextNode.CreateCollection();
            _joinableFactory = joinableTaskContextNode.CreateFactory(_joinableCollection);

            _errorListProvider = new AsyncLazy<ErrorListProvider>(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    return new ErrorListProvider(serviceProvider);
                },
                _joinableFactory);

            Reset();
        }

        public async Task InitializeAsync(IAsyncServiceProvider site)
        {
            await _joinableFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte = await site.GetDTEAsync();
                _solutionEvents = dte.Events.SolutionEvents;
                _solutionEvents.AfterClosing += SolutionEvents_AfterClosing;
#if VS15
                // these properties are specific to VS15 since they are use to attach to solution events
                // which is further used to start bg job runner to schedule auto restore
                await AdviseAsync(site);
#endif
            });
        }

        public void Dispose()
        {
            Reset(isDisposing: true);

            _joinableFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_solutionEvents != null)
                {
                    _solutionEvents.AfterClosing -= SolutionEvents_AfterClosing;
                }
#if VS15
                Unadvise();
#endif
                if (_errorListProvider.IsValueCreated)
                {
                    (await _errorListProvider.GetValueAsync()).Dispose();
                }
            });
        }

        private void Reset(bool isDisposing = false)
        {
            _workerCts?.Cancel();

            if (_backgroundJobRunner?.IsValueCreated == true)
            {
                // Do not block VS for more than 5 sec.
                _joinableFactory.Run(
                    () => Task.WhenAny(_backgroundJobRunner.Value, Task.Delay(TimeSpan.FromSeconds(5))));
            }

            _pendingRestore?.Dispose();
            _workerCts?.Dispose();

            if (_pendingRequests?.IsValueCreated == true)
            {
                _pendingRequests.Value.Dispose();
            }

            if (!isDisposing)
            {
                _workerCts = new CancellationTokenSource();

                _backgroundJobRunner = new Lazy<Task>(
                    valueFactory: () => Task.Run(
                        function: () => StartBackgroundJobRunnerAsync(_workerCts.Token),
                        cancellationToken: _workerCts.Token));

                _pendingRequests = new Lazy<BlockingCollection<SolutionRestoreRequest>>(
                    () => new BlockingCollection<SolutionRestoreRequest>(RequestQueueLimit));

                _pendingRestore = new BackgroundRestoreOperation(blockingUi: false);
                _activeRestoreTask = Task.FromResult(true);
                _restoreJobContext = new SolutionRestoreJobContext();
            }
        }

        private void SolutionEvents_AfterClosing()
        {
            Reset();
            ErrorListProvider.Tasks.Clear();
        }

        public async Task<bool> ScheduleRestoreAsync(
            SolutionRestoreRequest request, CancellationToken token)
        {
            if (_solutionManager.IsSolutionFullyLoaded)
            {
                // start background runner if not yet started
                // ignore the value
                var ignore = _backgroundJobRunner.Value;
            }

            var pendingRestore = _pendingRestore;

            // on-board request onto pending restore operation
            _pendingRequests.Value.TryAdd(request);

            using (_joinableCollection.Join())
            {
                return await (Task<bool>)pendingRestore;
            }
        }

        public bool Restore(SolutionRestoreRequest request)
        {
            return _joinableFactory.Run(
                async () =>
                {
                    using (var restoreOperation = new BackgroundRestoreOperation(blockingUi: true))
                    {
                        await PromoteTaskToActiveAsync(restoreOperation, _workerCts.Token);

                        var result = await ProcessRestoreRequestAsync(restoreOperation, request, _workerCts.Token);

                        return result;
                    }
                },
                JoinableTaskCreationOptions.LongRunning);
        }

        public void CleanCache()
        {
            Interlocked.Exchange(ref _restoreJobContext, new SolutionRestoreJobContext());
        }

        private async Task StartBackgroundJobRunnerAsync(CancellationToken token)
        {
            // Hops onto a background pool thread
            await TaskScheduler.Default;

            // Loops forever until it's get cancelled
            while (!token.IsCancellationRequested)
            {
                // Grabs a local copy of pending restore operation
                using (var restoreOperation = _pendingRestore)
                {
                    try
                    {
                        // Blocks the execution until first request is scheduled
                        // Monitors the cancelllation token as well.
                        var request = _pendingRequests.Value.Take(token);

                        token.ThrowIfCancellationRequested();

                        // Claims the ownership over the active task
                        // Awaits for currently running restore to complete
                        await PromoteTaskToActiveAsync(restoreOperation, token);

                        token.ThrowIfCancellationRequested();

                        // Drains the queue
                        while (!_pendingRequests.Value.IsCompleted
                            && !token.IsCancellationRequested)
                        {
                            SolutionRestoreRequest discard;
                            if (!_pendingRequests.Value.TryTake(out discard, IdleTimeoutMs, token))
                            {
                                break;
                            }
                        }

                        token.ThrowIfCancellationRequested();

                        // Replaces pending restore operation with a new one.
                        // Older value is ignored.
                        var ignore = Interlocked.CompareExchange(
                            ref _pendingRestore, new BackgroundRestoreOperation(blockingUi: false), restoreOperation);

                        token.ThrowIfCancellationRequested();

                        // Runs restore job with scheduled request params
                        await ProcessRestoreRequestAsync(restoreOperation, request, token);

                        // Repeats...
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // Ignores
                    }
                    catch (Exception e)
                    {
                        // Writes stack to activity log
                        _logger.LogError(e.ToString());
                        // Do not die just yet
                    }
                }
            }
        }

        private async Task<bool> ProcessRestoreRequestAsync(
            BackgroundRestoreOperation restoreOperation,
            SolutionRestoreRequest request,
            CancellationToken token)
        {
            // Start the restore job in a separate task on a background thread
            // it will switch into main thread when necessary.
            var joinableTask = _joinableFactory.RunAsync(
                () => StartRestoreJobAsync(request, restoreOperation.BlockingUI, token));

            var continuation = joinableTask
                .Task
                .ContinueWith(t => restoreOperation.ContinuationAction(t));

            return await joinableTask;
        }

        private async Task PromoteTaskToActiveAsync(BackgroundRestoreOperation restoreOperation, CancellationToken token)
        {
            var pendingTask = (Task<bool>)restoreOperation;

            int attempt = 0;
            for (var retry = true;
                retry && !token.IsCancellationRequested && attempt != PromoteAttemptsLimit;
                attempt++)
            {
                // Grab local copy of active task
                var activeTask = _activeRestoreTask;

                // Await for the completion of the active *unbound* task
                var cancelTcs = new TaskCompletionSource<bool>();
                using (var ctr = token.Register(() => cancelTcs.TrySetCanceled()))
                {
                    await Task.WhenAny(activeTask, cancelTcs.Task);
                }

                // Try replacing active task with the new one.
                // Retry from the beginning if the active task has changed.
                retry = Interlocked.CompareExchange(
                    ref _activeRestoreTask, pendingTask, activeTask) != activeTask;
            }

            if (attempt == PromoteAttemptsLimit)
            {
                throw new InvalidOperationException("Failed promoting pending task.");
            }
        }

        private async Task<bool> StartRestoreJobAsync(
            SolutionRestoreRequest jobArgs, bool blockingUi, CancellationToken token)
        {
            await TaskScheduler.Default;

            using (var jobCts = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var logger = await RestoreOperationLogger.StartAsync(
                _serviceProvider, ErrorListProvider, blockingUi, jobCts))
            using (var job = await SolutionRestoreJob.CreateAsync(
                _serviceProvider, logger, jobCts.Token))
            {
                return await job.ExecuteAsync(jobArgs, _restoreJobContext, jobCts.Token);
            }
        }

        public override int OnAfterBackgroundSolutionLoadComplete()
        {
            if (_pendingRequests.IsValueCreated)
            {
                // ensure background runner has started
                // ignore the value
                var ignore = _backgroundJobRunner.Value;
            }

            return VSConstants.S_OK;
        }

        private class BackgroundRestoreOperation
            : IEquatable<BackgroundRestoreOperation>, IDisposable
        {
            private readonly Guid _id = Guid.NewGuid();

            private TaskCompletionSource<bool> JobTcs { get; } = new TaskCompletionSource<bool>();

            private Task<bool> Task => JobTcs.Task;

            public System.Runtime.CompilerServices.TaskAwaiter<bool> GetAwaiter() => Task.GetAwaiter();

            public static explicit operator Task<bool>(BackgroundRestoreOperation restoreOperation) => restoreOperation.Task;

            public bool BlockingUI { get; }

            public BackgroundRestoreOperation(bool blockingUi)
            {
                BlockingUI = blockingUi;
            }

            public void ContinuationAction(Task<bool> targetTask)
            {
                // propagate the restore target task status to the *unbound* active task.
                if (targetTask.IsFaulted || targetTask.IsCanceled)
                {
                    // fail the restore result if the target task has failed or cancelled.
                    JobTcs.TrySetResult(result: false);
                }
                else
                {
                    // completed successfully
                    JobTcs.TrySetResult(targetTask.Result);
                }
            }

            public bool Equals(BackgroundRestoreOperation other)
            {
                if (ReferenceEquals(null, other))
                {
                    return false;
                }

                if (ReferenceEquals(this, other))
                {
                    return true;
                }

                return _id == other._id;
            }

            public override bool Equals(object obj) => Equals(obj as BackgroundRestoreOperation);

            public override int GetHashCode() => _id.GetHashCode();

            public static bool operator ==(BackgroundRestoreOperation left, BackgroundRestoreOperation right)
            {
                return Equals(left, right);
            }

            public static bool operator !=(BackgroundRestoreOperation left, BackgroundRestoreOperation right)
            {
                return !Equals(left, right);
            }

            public override string ToString() => _id.ToString();

            public void Dispose()
            {
                // Inner code block of using clause may throw an unhandled exception.
                // This'd result in leaving the active task in incomplete state.
                // Hence the next restore operation would hang forever.
                // To resolve potential deadlock issue the unbound task is to be completed here.
                if (!Task.IsCompleted && !Task.IsCanceled && !Task.IsFaulted)
                {
                    JobTcs.TrySetResult(result: false);
                }
            }
        }
    }
}
