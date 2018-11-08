using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration.Install;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AzureSqlDbInfluxPusher
{
    partial class AzureSqlDbInfluxPusherService : ServiceBase
    {
        private readonly HttpClient httpClient;

        private readonly Timer timer;

        private readonly List<string> databases = new List<string>();

        private readonly ConcurrentDictionary<string, DateTime> lastDbUpdate = new ConcurrentDictionary<string, DateTime>();

        private readonly object timerLock = new object();

        private readonly Settings settings;

        private DateTime previousMasterCollectTime;
        private DateTime lastDbListUpdate;
        private bool console;
        private bool shouldStop;
        private TimeSpan interval = TimeSpan.FromSeconds(60);
        private ILogger log;

        public AzureSqlDbInfluxPusherService(Settings settings)
        {
            this.ServiceName = Program.ServiceName;
            this.timer = new Timer(TimerCallback);
            this.httpClient = new HttpClient();
            if (!string.IsNullOrEmpty(settings.InfluxDbLogin))
            {
                this.httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{settings.InfluxDbLogin}:{settings.InfluxDbPassword}")));
            }

            this.settings = settings;
        }

        public void StartConsole(string[] args)
        {
            // TODO paramétrer l'application en mode console
            //this.interval = TimeSpan.FromSeconds(15);

            this.log = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Console()
                .CreateLogger();

            if (args.Any(a => string.Equals(a, "--install", StringComparison.OrdinalIgnoreCase)))
            {
                this.log.Information("Installing service...");
                ManagedInstallerClass.InstallHelper(new string[] { Assembly.GetExecutingAssembly().Location });
            }
            else if (args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                this.log.Information("Uninstalling service...");
                ManagedInstallerClass.InstallHelper(new string[] { "/u", Assembly.GetExecutingAssembly().Location });
            }
            else
            {
                this.log.Information("Starting program in console mode...");
                this.log.Information("Use the --send parameter to really send data to InfluxDB");
                this.log.Information("Use the --install and --uninstall parameter to manage the service");
                this.console = !args.Any(x => string.Equals("--send", x, StringComparison.OrdinalIgnoreCase));
                this.Run();
                this.log.Information("Service started, press any key to exit...");
                Console.ReadKey();
            }
        }

        protected override void OnStart(string[] args)
        {
            var logConfig = new LoggerConfiguration();

            if (!string.IsNullOrEmpty(settings.SerilogFileName))
            {
                logConfig = logConfig
                    .WriteTo.RollingFile(settings.SerilogFileName);
            }

            if (!string.IsNullOrEmpty(settings.SerilogSeqUri))
            {
                logConfig = logConfig
                    .WriteTo.Seq(settings.SerilogSeqUri, apiKey: settings.SerilogSeqApiKey);
            }

            this.log = logConfig
                .CreateLogger();

            this.log.Information("Starting AzureSqlDbInfluxPusher service");
            this.Run();
        }

        protected override void OnStop()
        {
            this.timer.Change(Timeout.Infinite, Timeout.Infinite);
            this.shouldStop = true;
        }

        private void Run()
        {
            this.timer.Change(TimeSpan.Zero, interval);
        }

        private void TimerCallback(object state)
        {
            if (this.shouldStop)
            {
                return;
            }

            // non reentrant
            if (Monitor.TryEnter(timerLock))
            {
                try
                {
                    DoAsync()
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception ex)
                {
                    log.Error(ex, "Unhandled exception Exception occured");
                }
                finally
                {
                    Monitor.Exit(timerLock);
                }
            }
            else
            {
                this.log.Verbose("Reentrant callback, skipping...");
            }
        }

        private async Task DoAsync()
        {
            var sw = new Stopwatch();
            sw.Start();
            await CollectMaster();
            await CollectDbs();
            sw.Stop();
            this.log.Verbose("Job completed in {Elapsed} seconds", sw.Elapsed.TotalSeconds);
        }

        private async Task CollectMaster()
        {
            var influxdb = new StringBuilder();

            var count = 0;
            var d = new Dictionary<string, Dictionary<DateTime, ResourceStatRow>>(StringComparer.Ordinal);
            using (var conn = OpenMaster())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "select * from sys.resource_stats where end_time > dateadd(minute, -20, getutcdate())";

                    var maxTime = DateTime.MinValue;

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (reader.Read())
                        {
                            var dbName = reader.GetString(2);

                            var r = new ResourceStatRow();
                            //var startTime = reader.GetDateTime(0);
                            r.Time = reader.GetDateTime(0); // start_time
                            //r.Time = reader.GetDateTime(1);
                            //var sku = reader.GetString(3);
                            r.Size = reader.GetDouble(4);
                            r.AvgCpuPercent = reader.GetDecimal(5);
                            r.AvgIOPercent = reader.GetDecimal(6);
                            r.AvgLogPercent = reader.GetDecimal(7);
                            //var maxWorkerPercent = reader.GetDecimal(8);
                            //var maxSessionPercent = reader.GetDecimal(9);
                            //var dtuLimit = reader.GetDecimal(10);
                            //var xtpStoragePercent = reader.GetDecimal(11);

                            //var r = new ResourceStatRow {  }

                            if (!d.TryGetValue(dbName, out Dictionary<DateTime, ResourceStatRow> rows))
                            {
                                rows = new Dictionary<DateTime, ResourceStatRow>();
                                d[dbName] = rows;
                            }

                            rows[r.Time] = r;

                            if (r.Time > maxTime)
                                maxTime = r.Time;
                        }
                    }

                    if (maxTime == DateTime.MinValue)
                    {
                        this.log.Warning("No resource_stats data for the last 20 minutes...");
                        return;
                    }

                    if (maxTime <= previousMasterCollectTime)
                    {
                        this.log.Verbose("Most recent data already sent, skipping...");
                        return;
                    }

                    this.log.Verbose("Sending data for time {0}", maxTime);

                    foreach (var dbPair in d)
                    {
                        var dbName = dbPair.Key;

                        foreach (var dbTime in dbPair.Value)
                        {
                            if (dbTime.Key > previousMasterCollectTime)
                            {
                                var row = dbTime.Value;
                                influxdb.AppendFormat(
                                    CultureInfo.InvariantCulture,
                                    "{0},db={1} size={2},avg_cpu={3},avg_io={4},avg_log={5} {6}\n",
                                    settings.InfluxDbMeasureResourceStats,
                                    dbName,
                                    row.Size,
                                    row.AvgCpuPercent,
                                    row.AvgIOPercent,
                                    row.AvgLogPercent,
                                    DateTimeToMinutesUnixTimestamp(dbTime.Key)
                                    );
                                count++;
                            }
                        }
                    }

                    previousMasterCollectTime = maxTime;
                }
            }

            if (count > 0)
            {
                if (this.console)
                {
                    this.log.Verbose("Console mode : {Count} rows should have been sent to InfluxDB", count);
                    return;
                }


                var content = new ByteArrayContent(Encoding.ASCII.GetBytes(influxdb.ToString()));
                var response = await httpClient.PostAsync($"{settings.InfluxDbUri}/write?db={settings.InfluxDbDatabase}&precision=m", content);
                if (!response.IsSuccessStatusCode)
                {
                    this.log.Error("Error while sending data to InfluxDB. Code {StatusCode} - {Reason}", response.StatusCode, response.ReasonPhrase);
                }
            }
        }

        private async Task CollectDbs()
        {
            if ((DateTime.Now - lastDbListUpdate).TotalMinutes > 60)
            {
                // mise à jour de la liste des bases de données toutes les heures
                try
                {
                    var previous = this.databases.Count;
                    await UpdateDbList();
                    this.log.Verbose("Mise à jour de la liste des bases de données : {Count} bases à surveiller (Ancienne valeur = {PreviousCount})", this.databases.Count, previous);
                }
                catch (Exception ex)
                {
                    this.log.Error(ex, "Erreur durant l'actualisation des bases de données à surveiller");
                }
            }

            var batch = new Batch(this.settings, this.log, this.console);

            var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };
            Parallel.ForEach(databases, options, async db =>
            {
                try
                {
                    await CollectDb(batch, db);
                }
                catch (Exception ex)
                {
                    this.log.Error(ex, "Erreur durant la collecte des métriques pour la base de données {Database}", db);
                }

            });

            await batch.Commit(httpClient, true);
        }

        private async Task CollectDb(Batch batch, string dbName)
        {
            if (!lastDbUpdate.TryGetValue(dbName, out var lastUpdate))
            {
                lastUpdate = DateTime.MinValue;
            }

            var writer = new StringBuilder();
            var count = 0;

            using (var conn = OpenDb(dbName))
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"select * from sys.dm_db_resource_stats";
                    using (var reader = cmd.ExecuteReader())
                    {
                        DateTime mostRecentTime = DateTime.MinValue;

                        while (reader.Read())
                        {
                            // end_time
                            var time = reader.GetDateTime(0);

                            if (time <= lastUpdate)
                            {
                                // stats list is in descending order
                                // as soon as we found a row older that the last stat, then we can consider we read all the new rows, and that we can stop reading results
                                break;
                            }

                            var cpu = reader.GetDecimal(1);         // Average compute utilization in percentage of the limit of the service tier.
                            var io = reader.GetDecimal(2);          // Average data I/O utilization in percentage of the limit of the service tier.
                            var log = reader.GetDecimal(3);         // Average write resource utilization in percentage of the limit of the service tier.
                            var mem = reader.GetDecimal(4);         // Average memory utilization in percentage of the limit of the service tier. This includes memory used for storage of In - Memory OLTP objects.
                            var xtp = reader.GetDecimal(5);         // Storage utilization for In-Memory OLTP in percentage of the limit of the service tier (at the end of the reporting interval).
                                                                    // This includes memory used for storage of the following In-Memory OLTP objects: memory-optimized tables, indexes, and table variables. It also includes memory used for processing ALTER TABLE operations.                                        
                                                                    // Returns 0 if In - Memory OLTP is not used in the database.
                            var worker = reader.GetDecimal(6);      // Maximum concurrent workers (requests) in percentage of the limit of the database’s service tier.
                            var session = reader.GetDecimal(7);     // Maximum concurrent sessions in percentage of the limit of the database’s service tier.

                            if (time > mostRecentTime)
                            {
                                mostRecentTime = time;
                            }

                            writer.AppendFormat(
                                CultureInfo.InvariantCulture,
                                "{0},db={1} avg_cpu={2},avg_io={3},avg_log={4},avg_mem={5},avg_xtp={6},max_worker={7},max_session={8} {9}\n",
                                "db_resource_stats",
                                dbName,
                                cpu,
                                io,
                                log,
                                mem,
                                xtp,
                                worker,
                                session,
                                DateTimeToSecondsUnixTimestamp(time)
                            );
                            count++;
                        }

                        lastDbUpdate[dbName] = mostRecentTime;
                    }
                }
            }

            batch.Append(writer, count, dbName);
            await batch.Commit(httpClient, false);
        }

        private async Task UpdateDbList()
        {
            using (var conn = OpenMaster())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "select name from sys.databases where database_id > 1 and state = 0";

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        databases.Clear();
                        while (reader.Read())
                        {
                            databases.Add(reader.GetString(0));
                        }
                    }

                    lastDbListUpdate = DateTime.Now;
                }
            }
        }

        private SqlConnection OpenMaster() => OpenDb("master");

        private SqlConnection OpenDb(string databaseName)
        {
            var conn = new SqlConnection();
            conn.ConnectionString = string.Format(settings.SqlConnectionString, databaseName);
            conn.Open();
            return conn;
        }

        private static int DateTimeToMinutesUnixTimestamp(DateTime dateTime)
        {
            return (int)(dateTime - new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)).TotalMinutes;
        }

        private static int DateTimeToSecondsUnixTimestamp(DateTime dateTime)
        {
            return (int)(dateTime - new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        class Batch
        {
            private readonly Settings settings;
            private readonly ILogger log;

            private readonly List<StringBuilder> builders = new List<StringBuilder>();

            private int rowCount;

            private bool console;

            private SemaphoreSlim commitLock = new SemaphoreSlim(1);

            private object appendLock = new object();

            public Batch(Settings settings, ILogger log, bool console)
            {
                this.settings = settings;
                this.log = log;
                this.console = console;
            }

            public void Append(StringBuilder builder, int count, string db)
            {
                if (count == 0)
                {
                    return;
                }

                lock (appendLock)
                {
                    //this.log.Verbose("Mode console : {Row} lignes de stats détaillées pour la base {DataBase}", count, db);
                    this.builders.Add(builder);
                    this.rowCount += count;
                }
            }

            public async Task Commit(HttpClient httpClient, bool force)
            {
                await commitLock.WaitAsync();
                try
                {
                    if (builders.Count > 0 && (force || rowCount > 8000))
                    {
                        using (var memStream = new MemoryStream(rowCount * 200))
                        using (var writer = new StreamWriter(memStream, Encoding.ASCII))
                        {
                            lock (appendLock)
                            {
                                if (this.console)
                                {
                                    this.log.Verbose("Console mode : {Row} rows should have been sent as detailed stat", this.rowCount);
                                }
                                else
                                {
                                    foreach (var builder in builders)
                                    {
                                        writer.Write(builder.ToString());
                                    }

                                    builders.Clear();
                                    rowCount = 0;
                                }
                            }

                            writer.Flush();
                            memStream.Position = 0;

                            using (var content = new StreamContent(memStream))
                            {
                                var response = await httpClient.PostAsync($"{settings.InfluxDbUri}/write?db={settings.InfluxDbDatabase}&precision=s", content);
                                if (!response.IsSuccessStatusCode)
                                {
                                    this.log.Error("Error while sending detailed stats to InfluxDB. Code {StatusCode} - {Reason}", response.StatusCode, response.ReasonPhrase);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    commitLock.Release();
                }
            }
        }

        class AsyncLock
        {
            private readonly Task<IDisposable> _releaserTask;
            private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
            private readonly IDisposable _releaser;

            public AsyncLock()
            {
                _releaser = new Releaser(_semaphore);
                _releaserTask = Task.FromResult(_releaser);
            }
            public IDisposable Lock()
            {
                _semaphore.Wait();
                return _releaser;
            }
            public Task<IDisposable> LockAsync()
            {
                var waitTask = _semaphore.WaitAsync();
                return waitTask.IsCompleted
                    ? _releaserTask
                    : waitTask.ContinueWith(
                        (_, releaser) => (IDisposable)releaser,
                        _releaser,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
            }
            private class Releaser : IDisposable
            {
                private readonly SemaphoreSlim _semaphore;
                public Releaser(SemaphoreSlim semaphore)
                {
                    _semaphore = semaphore;
                }
                public void Dispose()
                {
                    _semaphore.Release();
                }
            }
        }

        struct ResourceStatRow
        {
            public DateTime Time { get; set; }

            public double Size { get; set; }

            public decimal AvgCpuPercent { get; set; }

            public decimal AvgIOPercent { get; set; }

            public decimal AvgLogPercent { get; set; }
        }
    }
}
