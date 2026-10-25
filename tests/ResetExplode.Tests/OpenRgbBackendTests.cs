using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace ResetExplode.Tests;

/// <summary>Servidor de teste local: não toca no OpenRGB nem no hardware real.</summary>
public sealed class OpenRgbBackendTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepNaoRestauraModoNemAoEncerrar(bool keep)
    {
        using var server = new OpenRgbServer();
        var backend = new OpenRgbBackend();
        try
        {
            var (devices, _) = backend.Connect("127.0.0.1", server.Port);
            var device = Assert.Single(devices);
            Assert.Equal(0, backend.Acquire(device, true));
            Assert.Equal(0, backend.Send(device, new[] { ((byte)12, (byte)34, (byte)56) }));
            if (keep)
            {
                backend.SaveProfile("ResetExplode");
                backend.Keep(device);
            }
            backend.Release(device);
        }
        finally { backend.Shutdown(); }
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var packets = server.Packets.ToArray();
        Assert.Equal(keep ? 0 : 1, packets.Count(p => p.Type == 1101));
        Assert.Equal(new byte[] { 12, 34, 56, 0 }, Assert.Single(packets.Where(p => p.Type == 1050)).Payload[6..]);
        if (keep)
            Assert.Equal(Encoding.ASCII.GetBytes("ResetExplode\0"), Assert.Single(packets.Where(p => p.Type == 151)).Payload);
    }

    [Fact]
    public async Task ReativarDepoisDeKeepAtualizaEstadoQueSeraRestaurado()
    {
        using var server = new OpenRgbServer();
        var backend = new OpenRgbBackend();
        try
        {
            var (devices, _) = backend.Connect("127.0.0.1", server.Port);
            var device = Assert.Single(devices);
            Assert.Equal(0, backend.Acquire(device, true));
            backend.Keep(device);
            backend.Release(device);
            Assert.Equal(0, backend.Acquire(device, true));
            backend.Release(device);
        }
        finally { backend.Shutdown(); }
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var packets = server.Packets.ToArray();
        Assert.Equal(2, packets.Count(p => p.Type == 1));
        var restored = Assert.Single(packets.Where(p => p.Type == 1101));
        Assert.Contains("Deixado", Encoding.ASCII.GetString(restored.Payload));
        Assert.DoesNotContain("Original", Encoding.ASCII.GetString(restored.Payload));
    }

    [Fact]
    public void SalvarDesconectadoFalhaSemAnunciarSucesso()
    {
        Assert.Throws<IOException>(() => new OpenRgbBackend().SaveProfile("ResetExplode"));
    }

    private sealed class OpenRgbServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(10));
        public ConcurrentQueue<(uint Type, byte[] Payload)> Packets { get; } = new();
        public int Port { get; }
        public Task Completion { get; }

        public OpenRgbServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Completion = RunAsync();
        }

        private async Task RunAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync(_timeout.Token);
            using var stream = client.GetStream();
            int reads = 0;
            while (true)
            {
                var header = new byte[16];
                int first = await stream.ReadAsync(header.AsMemory(0, 1), _timeout.Token);
                if (first == 0) return;
                await stream.ReadExactlyAsync(header.AsMemory(1), _timeout.Token);
                Assert.Equal("ORGB", Encoding.ASCII.GetString(header, 0, 4));
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                var payload = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12))];
                await stream.ReadExactlyAsync(payload, _timeout.Token);
                Packets.Enqueue((type, payload));
                byte[]? reply = type switch
                {
                    40 => BitConverter.GetBytes(3u),
                    0 => BitConverter.GetBytes(1u),
                    1 => Controller(++reads == 1 ? "Original" : "Deixado"),
                    _ => null,
                };
                if (reply == null) continue;
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)reply.Length);
                await stream.WriteAsync(header, _timeout.Token);
                await stream.WriteAsync(reply, _timeout.Token);
            }
        }

        private static byte[] Controller(string mode)
        {
            using var data = new MemoryStream();
            using var writer = new BinaryWriter(data, Encoding.ASCII, leaveOpen: true);
            void Text(string value)
            {
                byte[] bytes = Encoding.ASCII.GetBytes(value + "\0");
                writer.Write((ushort)bytes.Length);
                writer.Write(bytes);
            }
            writer.Write(0u); writer.Write(0);
            Text("Teste"); Text("Fabricante"); Text(""); Text(""); Text(""); Text("");
            writer.Write((ushort)1); writer.Write(0);
            Text(mode); writer.Write(0);
            for (int i = 0; i < 11; i++) writer.Write(0u);
            writer.Write((ushort)0); // cores do modo
            writer.Write((ushort)0); // zonas
            writer.Write((ushort)1); // LEDs
            byte[] result = data.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)result.Length);
            return result;
        }

        public void Dispose()
        {
            _timeout.Cancel();
            _listener.Stop();
            _timeout.Dispose();
        }
    }
}
