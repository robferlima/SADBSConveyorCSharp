using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;

using System.Threading;
using System.Threading.Tasks;
using System.IO;

using SADBSConveyorLib;

namespace SADBSConveyor
{
    public partial class Service1 : ServiceBase
    {
        private Task ConveyTask;
        private CancellationTokenSource CancelTokenSource;
        private Logging MainLog;
        private ConveyManager Manager;

        public Service1()
        {
            InitializeComponent();
            this.ServiceName = "SADBSConveyor";
            this.EventLog.Log = "Application";

            this.CanHandlePowerEvent = false;
            this.CanHandleSessionChangeEvent = false;
            this.CanPauseAndContinue = false;
            this.CanShutdown = false;
            this.CanStop = true;
            this.AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            this.MainLog = new Logging();
            var logPath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "logs");
            // MainLog.Initialize(logPath, "0-");

            Settings.Load(MainLog);

            MainLog.WriteEntry("SADBSConveyor Service Starting", DisplayAsType.Information);

            CancelTokenSource = new CancellationTokenSource();
            Manager = new ConveyManager();
            ConveyTask = Task.Factory.StartNew(() => Manager.Start(this.MainLog, CancelTokenSource.Token));
        }

        protected override void OnStop()
        {
            MainLog.WriteEntry("SADBSConveyor Service Stopping.", DisplayAsType.Information); 
            CancelTokenSource.Cancel();

            if (!ConveyTask.IsFaulted)
            {
                ConveyTask.Wait(); // wait for conveying to shut down cleanly.
            }

            Manager.UpdateServiceStatus("SADBSConveyor Service Stopped.");
            MainLog.WriteEntry("SADBSConveyor Service Stopped.", DisplayAsType.Information);
            MainLog = null;
        }

        // used by dev ui
        public void DevStart()
        {
            OnStart(null);
        }
        // used by devui
        public void DevStop()
        {
            OnStop();
        }

    }
}
