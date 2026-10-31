using System.Net;
using System.Net.Sockets;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Azure.Storage.Blobs;
using Declarad.Cli;
using Declarad.Config;
using Declarad.Hcl;
using Declarad.State;
using Declarad.State.Backends;
using Microsoft.Extensions.Logging.Abstractions;

namespace Declarad.Tests
{
    public class BackendTests
    {
        private static StateFile SampleState(string address = "group.g")
        {
            var state = new StateFile();
            state.Upsert(new StateResource { Address = address, Type = "group", Guid = Guid.NewGuid(), Dn = "CN=g,CN=Users,DC=corp,DC=local" });
            state.Outputs["x"] = Value.From("y");
            return state;
        }

        /// <summary>Exercises read/write/lock semantics every backend must provide.</summary>
        internal static void CheckBackendContract(Func<IStateBackend> create)
        {
            var backend = create();
            Assert.Null(backend.Read());

            var manager = new StateManager(backend);
            var state = manager.Load();
            state.Upsert(SampleState().Resources[0]);
            manager.Save(state);
            var read = create().Read()!;
            Assert.Equal(1, read.Serial);
            Assert.Equal(state.Lineage, read.Lineage);
            Assert.Single(read.Resources);

            // A second process that loaded earlier cannot overwrite newer state.
            var stale = new StateManager(create());
            var staleState = stale.Load();
            manager.Save(state);
            Assert.Contains("changed by another run", Assert.Throws<DeclaradException>(() => stale.Save(staleState)).Message);

            // Locking: a second locker is refused and told who holds the lock.
            var other = create();
            using (manager.Lock("apply", TimeSpan.Zero))
            {
                var locked = Assert.Throws<StateLockedException>(() => other.Lock(new LockInfo { Operation = "plan" }));
                Assert.Equal("apply", locked.Holder?.Operation);
                manager.Save(state); // the lock holder can still write
            }
            var info = new LockInfo { Operation = "crashed" };
            other.Lock(info);
            Assert.Throws<StateLockedException>(() => create().Lock(new LockInfo()));
            create().ForceUnlock(info.Id);
            var again = new LockInfo();
            create().Lock(again);
            create().Unlock(again.Id);
            Assert.Equal(3, create().Read()!.Serial);
        }

        [Fact]
        public void LocalBackendHonoursTheContract()
        {
            string dir = System.IO.Directory.CreateTempSubdirectory("dad-local").FullName;
            CheckBackendContract(() => new LocalBackend(Path.Combine(dir, "state", "s.json")));
            Assert.True(File.Exists(Path.Combine(dir, "state", "s.json.backup")));
        }

        [Fact]
        public void HttpBackendSpeaksTerraformsProtocol()
        {
            using var server = new FakeHttpBackendServer();
            var config = new BackendConfig("http", new Dictionary<string, string>
            {
                ["address"] = server.Url + "state",
                ["lock_address"] = server.Url + "state",
                ["unlock_address"] = server.Url + "state",
                ["username"] = "ci",
                ["password"] = "secret",
            });
            CheckBackendContract(() => BackendFactory.Create(config, ".", _ => null));
            Assert.All(server.AuthHeaders, h => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ci:secret")), h));
            Assert.Contains(server.Methods, m => m == "LOCK");
            Assert.Contains(server.Queries, q => q.Contains("ID="));
        }

        [IntegrationFact("DECLARAD_TEST_S3_ENDPOINT")]
        public void S3BackendHonoursTheContract()
        {
            string endpoint = Environment.GetEnvironmentVariable("DECLARAD_TEST_S3_ENDPOINT")!;
            string bucket = "dad-" + Guid.NewGuid().ToString("N")[..12];
            using (var s3 = new AmazonS3Client(new BasicAWSCredentials("test", "test"), new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" }))
            {
                s3.PutBucketAsync(bucket).GetAwaiter().GetResult();
            }
            var config = new BackendConfig("s3", new Dictionary<string, string>
            {
                ["bucket"] = bucket,
                ["key"] = "env/prod/declarad.tfstate",
                ["region"] = "us-east-1",
                ["endpoint"] = endpoint,
                ["use_path_style"] = "true",
                ["access_key"] = "test",
                ["secret_key"] = "test",
                ["encrypt"] = "true",
            });
            CheckBackendContract(() => BackendFactory.Create(config, ".", _ => null));
        }

        [IntegrationFact("DECLARAD_TEST_AZURITE")]
        public void AzureBackendHonoursTheContract()
        {
            string connectionString = Environment.GetEnvironmentVariable("DECLARAD_TEST_AZURITE")!;
            string container = "dad-" + Guid.NewGuid().ToString("N")[..12];
            new BlobContainerClient(connectionString, container).Create();
            var config = new BackendConfig("azurerm", new Dictionary<string, string>
            {
                ["connection_string"] = connectionString,
                ["container_name"] = container,
                ["key"] = "prod.declarad.tfstate",
            });
            CheckBackendContract(() => BackendFactory.Create(config, ".", _ => null));
        }

        [Fact]
        public void ReadsDeclaradBlockAndBackendOverrides()
        {
            var body = Parser.ParseFile("""
                declarad {
                  required_version = ">= 0.2, < 9.0"
                  backend "s3" {
                    bucket = "corp-state"
                    key    = "ad/${env("STAGE")}.tfstate"
                    region = "eu-west-1"
                    endpoints {
                      s3 = "https://s3.example"
                    }
                  }
                }
                """, "main.dad");
            var settings = ProjectSettings.FromBodies(new[] { body }, new Functions(".", n => n == "STAGE" ? "prod" : null));
            settings.CheckVersion();
            var backend = settings.Backend!;
            Assert.Equal("s3", backend.Type);
            Assert.Equal("ad/prod.tfstate", backend.Get("key"));
            Assert.Equal("https://s3.example", backend.Get("endpoints.s3"));

            string dir = System.IO.Directory.CreateTempSubdirectory("dad-bc").FullName;
            File.WriteAllText(Path.Combine(dir, "prod.backend.hcl"), "bucket = \"other\"\n");
            var overridden = ProjectSettings.ApplyOverrides(backend, new[] { "prod.backend.hcl", "region=us-east-2" }, dir, _ => null);
            Assert.Equal(("other", "us-east-2"), (overridden.Get("bucket"), overridden.Get("region")));
            Assert.NotEqual(backend.Hash(), overridden.Hash());

            var tooNew = ProjectSettings.FromBodies(new[] { Parser.ParseFile("declarad {\n  required_version = \">= 99.0\"\n}\n", "v.dad") }, new Functions("."));
            Assert.Contains("requires declarad >= 99.0", Assert.Throws<DeclaradException>(tooNew.CheckVersion).Message);
            Assert.Contains("no setting 'buckett'", Assert.Throws<DeclaradException>(() =>
                BackendFactory.Create(new BackendConfig("s3", new Dictionary<string, string> { ["buckett"] = "x" }), ".", _ => null)).Message);
        }

        [Theory]
        [InlineData(">= 0.2", "0.3.0", true)]
        [InlineData(">= 0.4", "0.3.0", false)]
        [InlineData("~> 0.3", "0.9.1", true)]
        [InlineData("~> 0.3", "1.0.0", false)]
        [InlineData("~> 0.3.1", "0.3.9", true)]
        [InlineData("~> 0.3.1", "0.4.0", false)]
        [InlineData("!= 0.3.0, >= 0.2", "0.3.0", false)]
        [InlineData("= 0.3.0", "0.3.0", true)]
        public void ChecksVersionConstraints(string constraint, string version, bool expected) =>
            Assert.Equal(expected, VersionConstraint.Satisfied(constraint, version));

        [Fact]
        public void InitMigratesStateBetweenBackends()
        {
            string dir = System.IO.Directory.CreateTempSubdirectory("dad-init").FullName;
            File.WriteAllText(Path.Combine(dir, "main.dad"), ConfigTests.Connection + "group \"g\" {}\n");
            StateStore.Save(Path.Combine(dir, StateStore.DefaultFileName), SampleState());
            var commands = new Commands(NullLoggerFactory.Instance);
            var g = new GlobalOptions { Dir = dir, NoColor = true };

            Assert.Equal(0, commands.Init(g));
            File.WriteAllText(Path.Combine(dir, "backend.dad"), "declarad {\n  backend \"local\" {\n    path = \"shared/team.json\"\n  }\n}\n");

            // Commands refuse to run until the new backend is initialized.
            Assert.Equal(1, commands.Output(g));
            Assert.Equal(1, commands.Init(g));
            Assert.Equal(0, commands.Init(g, migrateState: true));
            var migrated = StateStore.Load(Path.Combine(dir, "shared", "team.json"));
            Assert.Equal("group.g", Assert.Single(migrated.Resources).Address);
            Assert.Equal(0, commands.Output(g));

            // Locks held by a crashed run are reported, then released with force-unlock.
            var info = new LockInfo { Operation = "apply" };
            new LocalBackend(Path.Combine(dir, "shared", "team.json")).Lock(info);
            Assert.Equal(1, new StateCommands(NullLoggerFactory.Instance).Remove(g, new[] { "group.g" }));
            Assert.Equal(0, commands.ForceUnlock(g, info.Id, force: true));
            Assert.Equal(0, new StateCommands(NullLoggerFactory.Instance).Remove(g, new[] { "group.g" }));
        }

        /// <summary>A minimal server implementing Terraform's HTTP backend protocol.</summary>
        private sealed class FakeHttpBackendServer : IDisposable
        {
            private readonly HttpListener _listener = new();
            private readonly CancellationTokenSource _stop = new();
            private string? _state;
            private string? _lock;
            public string Url { get; }
            public List<string> Methods { get; } = new();
            public List<string> Queries { get; } = new();
            public List<string?> AuthHeaders { get; } = new();

            public FakeHttpBackendServer()
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                Url = $"http://127.0.0.1:{port}/";
                _listener.Prefixes.Add(Url);
                _listener.Start();
                Task.Run(Serve);
            }

            private async Task Serve()
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        ctx = await _listener.GetContextAsync();
                    }
                    catch (Exception) when (_stop.IsCancellationRequested)
                    {
                        return;
                    }
                    string body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                    lock (Methods)
                    {
                        Methods.Add(ctx.Request.HttpMethod);
                        Queries.Add(ctx.Request.Url!.Query);
                        AuthHeaders.Add(ctx.Request.Headers["Authorization"]);
                    }
                    var response = ctx.Response;
                    switch (ctx.Request.HttpMethod)
                    {
                        case "GET":
                            response.StatusCode = _state == null ? 404 : 200;
                            await Write(response, _state ?? "");
                            break;
                        case "POST":
                            _state = body;
                            response.StatusCode = 200;
                            response.Close();
                            break;
                        case "LOCK":
                            if (_lock != null)
                            {
                                response.StatusCode = 423;
                                await Write(response, _lock);
                            }
                            else
                            {
                                _lock = body;
                                response.StatusCode = 200;
                                response.Close();
                            }
                            break;
                        case "UNLOCK":
                            _lock = null;
                            response.StatusCode = 200;
                            response.Close();
                            break;
                        default:
                            response.StatusCode = 405;
                            response.Close();
                            break;
                    }
                }
            }

            private static async Task Write(HttpListenerResponse response, string text)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await response.OutputStream.WriteAsync(bytes);
                response.Close();
            }

            public void Dispose()
            {
                _stop.Cancel();
                _listener.Stop();
            }
        }
    }
}
