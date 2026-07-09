using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Text.Json;
using System.Threading.Channels;

namespace ModelContextProtocol.Tests.Client;

/// <summary>
/// Connection-flow tests for the 2026-07-28 protocol revision (SEP-2575 + SEP-2567)
/// on <see cref="McpClient"/>. A client that requests
/// <see cref="McpHttpHeaders.July2026ProtocolVersion"/> calls <c>server/discover</c> rather than
/// <c>initialize</c>.
/// </summary>
public class July2026ProtocolConnectionTests : ClientServerTestBase
{
    private const string LatestStableVersion = "2025-11-25";

    public July2026ProtocolConnectionTests(ITestOutputHelper testOutputHelper)
        : base(testOutputHelper, startServer: false)
    {
    }

    protected override void ConfigureServices(ServiceCollection services, IMcpServerBuilder mcpServerBuilder)
    {
        services.Configure<McpServerOptions>(options =>
        {
            options.ServerInfo = new Implementation { Name = nameof(July2026ProtocolConnectionTests), Version = "1.0" };
        });
    }

    [Fact]
    public async Task Client_RequestingJuly2026Protocol_NegotiatesIt()
    {
        StartServer();

        var options = new McpClientOptions { ProtocolVersion = McpHttpHeaders.July2026ProtocolVersion };
        await using var client = await CreateMcpClientForServer(options);

        Assert.Equal(McpHttpHeaders.July2026ProtocolVersion, client.NegotiatedProtocolVersion);
        Assert.NotNull(client.ServerCapabilities);
        Assert.Equal(nameof(July2026ProtocolConnectionTests), client.ServerInfo.Name);
    }

    [Fact]
    public async Task Client_RequestingLegacyVersion_NegotiatesLegacy()
    {
        StartServer();

        await using var client = await CreateMcpClientForServer(new McpClientOptions { ProtocolVersion = LatestStableVersion });

        Assert.NotEqual(McpHttpHeaders.July2026ProtocolVersion, client.NegotiatedProtocolVersion);
    }

    [Fact]
    public async Task LegacyClient_CanCallServerDiscover()
    {
        // server/discover is registered unconditionally, so a legacy client can probe it
        // (e.g., to learn capabilities without doing a second initialize).
        StartServer();

        await using var client = await CreateMcpClientForServer(new McpClientOptions { ProtocolVersion = LatestStableVersion });

        var response = await client.SendRequestAsync(
            new JsonRpcRequest { Method = RequestMethods.ServerDiscover },
            TestContext.Current.CancellationToken);

        var discoverResult = JsonSerializer.Deserialize<DiscoverResult>(response.Result, McpJsonUtilities.DefaultOptions);
        Assert.NotNull(discoverResult);
        Assert.NotEmpty(discoverResult.SupportedVersions);
        Assert.Contains(LatestStableVersion, discoverResult.SupportedVersions);
        Assert.Equal(nameof(July2026ProtocolConnectionTests), discoverResult.ServerInfo.Name);
    }

    [Fact]
    public async Task ServerDiscover_IncludesJuly2026ProtocolVersion()
    {
        StartServer();

        await using var client = await CreateMcpClientForServer();

        var response = await client.SendRequestAsync(
            new JsonRpcRequest { Method = RequestMethods.ServerDiscover },
            TestContext.Current.CancellationToken);

        var discoverResult = JsonSerializer.Deserialize<DiscoverResult>(response.Result, McpJsonUtilities.DefaultOptions);
        Assert.NotNull(discoverResult);
        Assert.Contains(McpHttpHeaders.July2026ProtocolVersion, discoverResult.SupportedVersions);
    }

    [Fact]
    public async Task Client_WithPriorDiscoverResult_SkipsServerDiscover()
    {
        StartServer();

        var ct = TestContext.Current.CancellationToken;
        await using var transport = new RecordingClientTransport();
        var priorDiscoverResult = CreatePriorDiscoverResult();

        await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            PriorDiscoverResult = priorDiscoverResult,
        }, loggerFactory: LoggerFactory, cancellationToken: ct);

        Assert.Empty(transport.SentMethods);
        Assert.Equal(McpHttpHeaders.July2026ProtocolVersion, client.NegotiatedProtocolVersion);
        Assert.Equal(priorDiscoverResult.ServerInfo.Name, client.ServerInfo.Name);
        Assert.NotSame(priorDiscoverResult.Capabilities, client.ServerCapabilities);
        Assert.NotNull(client.ServerCapabilities.Tools);

        var reusableDiscoverResult = client.GetDiscoverResult();
        Assert.NotSame(priorDiscoverResult, reusableDiscoverResult);
        Assert.NotSame(priorDiscoverResult.SupportedVersions, reusableDiscoverResult.SupportedVersions);
        Assert.NotSame(client.ServerCapabilities, reusableDiscoverResult.Capabilities);
        Assert.NotSame(client.ServerInfo, reusableDiscoverResult.ServerInfo);
        Assert.Equal(priorDiscoverResult.SupportedVersions, reusableDiscoverResult.SupportedVersions);
        Assert.Equal(priorDiscoverResult.TimeToLive, reusableDiscoverResult.TimeToLive);
        Assert.Equal(priorDiscoverResult.CacheScope, reusableDiscoverResult.CacheScope);

        reusableDiscoverResult.SupportedVersions.Clear();
        Assert.NotEmpty(client.GetDiscoverResult().SupportedVersions);
        reusableDiscoverResult.Capabilities.Tools = null;
        reusableDiscoverResult.ServerInfo.Name = "mutated";
        Assert.NotNull(client.GetDiscoverResult().Capabilities.Tools);
        Assert.Equal(priorDiscoverResult.ServerInfo.Name, client.ServerInfo.Name);
    }

    [Fact]
    public async Task Client_WithPriorDiscoverResult_AndPinnedModernVersion_UsesPinnedVersion()
    {
        StartServer();

        var ct = TestContext.Current.CancellationToken;
        await using var transport = new RecordingClientTransport();

        await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ProtocolVersion = McpHttpHeaders.July2026ProtocolVersion,
            PriorDiscoverResult = CreatePriorDiscoverResult(),
        }, loggerFactory: LoggerFactory, cancellationToken: ct);

        Assert.Empty(transport.SentMethods);
        Assert.Equal(McpHttpHeaders.July2026ProtocolVersion, client.NegotiatedProtocolVersion);
    }

    [Fact]
    public async Task Client_WithPriorDiscoverResult_AndLegacyProtocolVersion_Throws()
    {
        StartServer();

        var ct = TestContext.Current.CancellationToken;
        await using var transport = new RecordingClientTransport();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ProtocolVersion = LatestStableVersion,
                PriorDiscoverResult = CreatePriorDiscoverResult(),
            }, loggerFactory: LoggerFactory, cancellationToken: ct);
        });

        Assert.Empty(transport.SentMethods);
    }

    [Fact]
    public async Task Client_WithPriorDiscoverResult_AndNoCompatibleModernVersion_Throws()
    {
        StartServer();

        var ct = TestContext.Current.CancellationToken;
        await using var transport = new RecordingClientTransport();

        var exception = await Assert.ThrowsAsync<McpException>(async () =>
        {
            await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                PriorDiscoverResult = CreatePriorDiscoverResult([LatestStableVersion]),
            }, loggerFactory: LoggerFactory, cancellationToken: ct);
        });

        Assert.Contains(McpHttpHeaders.July2026ProtocolVersion, exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.SentMethods);
    }

    [Fact]
    public async Task Client_WithPriorDiscoverResult_AndPinnedModernVersionMissingFromPrior_Throws()
    {
        StartServer();

        var ct = TestContext.Current.CancellationToken;
        await using var transport = new RecordingClientTransport();

        var exception = await Assert.ThrowsAsync<McpException>(async () =>
        {
            await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ProtocolVersion = McpHttpHeaders.July2026ProtocolVersion,
                PriorDiscoverResult = CreatePriorDiscoverResult(["2026-08-01"]),
            }, loggerFactory: LoggerFactory, cancellationToken: ct);
        });

        Assert.Contains(McpHttpHeaders.July2026ProtocolVersion, exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.SentMethods);
    }

    private static DiscoverResult CreatePriorDiscoverResult(IList<string>? supportedVersions = null)
    {
        return new DiscoverResult
        {
            SupportedVersions = supportedVersions ?? [McpHttpHeaders.July2026ProtocolVersion, LatestStableVersion],
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability(),
            },
            ServerInfo = new Implementation { Name = "prior-discovery-test-server", Version = "1.0" },
            Instructions = "Use prior knowledge.",
            TimeToLive = TimeSpan.FromMinutes(10),
            CacheScope = CacheScope.Private,
        };
    }

    private sealed class RecordingClientTransport : IClientTransport, ITransport
    {
        private readonly Channel<JsonRpcMessage> _incomingToClient = Channel.CreateUnbounded<JsonRpcMessage>();
        private readonly List<string> _sentMethods = [];

        public string Name => "recording-client-transport";

        public ChannelReader<JsonRpcMessage> MessageReader => _incomingToClient.Reader;

        public bool IsConnected { get; private set; } = true;

        public string? SessionId => null;

        public IReadOnlyList<string> SentMethods => _sentMethods;

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult<ITransport>(this);

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            if (message is JsonRpcRequest request)
            {
                _sentMethods.Add(request.Method);

                if (request.Method == RequestMethods.ServerDiscover)
                {
                    _incomingToClient.Writer.TryWrite(new JsonRpcError
                    {
                        Id = request.Id,
                        Error = new JsonRpcErrorDetail
                        {
                            Code = (int)McpErrorCode.MethodNotFound,
                            Message = "Method not found",
                        },
                    });
                }
                else if (request.Method == RequestMethods.Initialize)
                {
                    _incomingToClient.Writer.TryWrite(new JsonRpcResponse
                    {
                        Id = request.Id,
                        Result = JsonSerializer.SerializeToNode(new InitializeResult
                        {
                            ProtocolVersion = LatestStableVersion,
                            Capabilities = new ServerCapabilities(),
                            ServerInfo = new Implementation { Name = "legacy-test-server", Version = "1.0" },
                        }, McpJsonUtilities.DefaultOptions),
                    });
                }
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _incomingToClient.Writer.TryComplete();
            IsConnected = false;
            return default;
        }
    }
}
