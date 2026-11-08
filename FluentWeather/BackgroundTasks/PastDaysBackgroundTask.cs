using System.Linq;
using System.Threading.Tasks;
using FluentWeather.Services;
using Windows.ApplicationModel.Background;

namespace FluentWeather.BackgroundTasks
{
    /// <summary>
    /// Runs every 15 minutes, the shortest interval Windows allows, and downloads the "Past days"
    /// captures that are due. When none is due it returns at once and downloads nothing.
    /// </summary>
    public sealed class PastDaysBackgroundTask : BackgroundTask
    {
        private BackgroundTaskDeferral? _deferral;

        public override void Register()
        {
            const string taskName = nameof(PastDaysBackgroundTask);
            if (BackgroundTaskRegistration.AllTasks.Any(t => t.Value.Name == taskName))
                return;

            var builder = new BackgroundTaskBuilder
            {
                Name = taskName,
                IsNetworkRequested = true,
                CancelOnConditionLoss = true
            };

            builder.SetTrigger(new TimeTrigger(15, false));
            builder.AddCondition(new SystemCondition(SystemConditionType.InternetAvailable));
            builder.Register();
        }

        protected override Task RunAsyncInternal(IBackgroundTaskInstance taskInstance)
        {
            if (taskInstance == null)
                return Task.CompletedTask;

            _deferral = taskInstance.GetDeferral();

            return Task.Run(async () =>
            {
                try
                {
                    await PastDaysService.RunDueCapturesAsync();
                }
                finally
                {
                    _deferral.Complete();
                }
            });
        }

        protected override void OnCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            // A capture that was cut off is downloaded at the next run.
        }
    }
}
