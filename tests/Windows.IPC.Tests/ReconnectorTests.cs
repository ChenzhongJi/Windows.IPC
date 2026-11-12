using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Client;
using Xunit;

namespace Windows.IPC.Tests
{
    public class ReconnectorTests
    {
        [Fact]
        public async Task Start_DuringSuccessfulAttempt_RunsPendingAttempt()
        {
            var firstAttemptStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstAttempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondAttemptStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var attemptCount = 0;
            using (var reconnector = new Reconnector(ReconnectionPolicy.Fast, async cancellationToken =>
            {
                if (Interlocked.Increment(ref attemptCount) == 1)
                {
                    firstAttemptStarted.TrySetResult(true);
                    return await releaseFirstAttempt.Task;
                }

                secondAttemptStarted.TrySetResult(true);
                return true;
            }))
            {
                reconnector.Start();
                Assert.Same(firstAttemptStarted.Task, await Task.WhenAny(firstAttemptStarted.Task, Task.Delay(5000)));

                reconnector.Start();
                reconnector.Start();
                releaseFirstAttempt.TrySetResult(true);

                Assert.Same(secondAttemptStarted.Task, await Task.WhenAny(secondAttemptStarted.Task, Task.Delay(5000)));
                await reconnector.StopAsync();
                Assert.Equal(2, attemptCount);
            }
        }

        [Fact]
        public async Task Stop_WithPendingAttempt_DoesNotRestart()
        {
            var firstAttemptStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstAttempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var attemptCount = 0;
            using (var reconnector = new Reconnector(ReconnectionPolicy.Fast, async cancellationToken =>
            {
                Interlocked.Increment(ref attemptCount);
                firstAttemptStarted.TrySetResult(true);
                return await releaseFirstAttempt.Task;
            }))
            {
                reconnector.Start();
                Assert.Same(firstAttemptStarted.Task, await Task.WhenAny(firstAttemptStarted.Task, Task.Delay(5000)));
                reconnector.Start();
                var firstStopTask = reconnector.StopAsync();
                var secondStopTask = reconnector.StopAsync();
                reconnector.Start();
                releaseFirstAttempt.TrySetResult(true);

                var stopTasks = Task.WhenAll(firstStopTask, secondStopTask);
                Assert.Same(stopTasks, await Task.WhenAny(stopTasks, Task.Delay(5000)));
                await stopTasks;
                Assert.Equal(1, attemptCount);
                Assert.False(reconnector.IsReconnecting);
            }
        }
    }
}
