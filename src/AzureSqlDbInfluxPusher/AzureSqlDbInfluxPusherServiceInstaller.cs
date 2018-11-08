using System.ComponentModel;
using System.Configuration.Install;

namespace AzureSqlDbInfluxPusher
{
    [RunInstaller(true)]
    public class AzureSqlDbInfluxPusherServiceInstaller : Installer
    {
        public AzureSqlDbInfluxPusherServiceInstaller()
        {
            var processInstaller = new System.ServiceProcess.ServiceProcessInstaller()
            {
                Account = System.ServiceProcess.ServiceAccount.LocalSystem
            };

            this.Installers.Add(processInstaller);

            var installer = new System.ServiceProcess.ServiceInstaller()
            {
                ServiceName = Program.ServiceName,
                Description = "Automatically send Azure SQL Database metrics to InfluxDB",
                DisplayName = "AzureSQLDb InfluxPusher",
                StartType = System.ServiceProcess.ServiceStartMode.Automatic,
                DelayedAutoStart = true
            };

            this.Installers.Add(installer);

        }
    }
}
