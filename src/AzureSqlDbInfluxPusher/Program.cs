using System;
using System.Configuration;
using System.ServiceProcess;

namespace AzureSqlDbInfluxPusher
{
    class Program
    {
        internal const string ServiceName = "InfluxPusher";

        static void Main(string[] args)
        {
            var appSettings = ConfigurationManager.AppSettings;

            var settings = new Settings
            {
                InfluxDbUri = appSettings["InfluxDb.Url"],
                InfluxDbLogin = appSettings["InfluxDb.Login"],
                InfluxDbPassword = appSettings["InfluxDb.Password"],
                InfluxDbDatabase = appSettings["InfluxDb.Database"],

                InfluxDbMeasureResourceStats = appSettings["InfluxDb.Measure.ResourceStats"],
                InfluxDbMeasureDatabaseResourceStats = appSettings["InfluxDb.Measure.DatabaseResourceStats"],

                SerilogFileName = appSettings["Serilog.FileName"],
                SerilogSeqUri = appSettings["Serilog.Seq.Url"],
                SerilogSeqApiKey = appSettings["Serilog.Seq.ApiKey"]
            };

            var connectionString = ConfigurationManager.ConnectionStrings[appSettings["Sql.ConnectionStringName"]];

            settings.SqlConnectionString = connectionString.ConnectionString;

            if (Environment.UserInteractive)
            {
                var service = new AzureSqlDbInfluxPusherService(settings);
                service.StartConsole(args);
            }
            else
            {
                Console.WriteLine("Starting program as a service...");
                Console.WriteLine("To run the program in console mode, use -c or --console argument");
                ServiceBase.Run(new ServiceBase[] { new AzureSqlDbInfluxPusherService(settings) });
            }
        }
    }
}