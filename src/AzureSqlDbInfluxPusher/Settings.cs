namespace AzureSqlDbInfluxPusher
{
    public class Settings
    {
        public string SqlConnectionString { get; set; }


        public string InfluxDbUri { get; set; }

        public string InfluxDbLogin { get; set; }

        public string InfluxDbPassword { get; set; }

        public string InfluxDbDatabase { get; set; }

        public string InfluxDbMeasureDatabaseResourceStats { get; set; }

        public string InfluxDbMeasureResourceStats { get; set; }

        public string SerilogFileName { get; set; }

        public string SerilogSeqUri { get; set; }

        public string SerilogSeqApiKey { get; set; }
    }
}
