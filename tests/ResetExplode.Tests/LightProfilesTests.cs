using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ResetExplode.Tests;

public sealed class LightProfilesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("padrão")]
    [InlineData(" PADRÃO ")]
    [InlineData("12345678901234567890123456789012345678901")]
    public void RecusaNomeInvalido(string name)
    {
        var settings = new AppSettings();
        LightProfiles.Ensure(settings);
        Assert.Throws<ArgumentException>(() => LightProfiles.Create(settings, name));
        Assert.Single(settings.LightProfiles);
    }

    [Fact]
    public void NovoCopiaTodosOsCamposEViraAtivo()
    {
        var settings = new AppSettings();
        var original = LightProfiles.Active(settings);
        original.Effect = "breath";
        original.R = 12; original.G = 34; original.B = 56;
        original.Brightness = 42; original.Speed = 8; original.Exclusive = false;
        original.Unselected.Add("OpenRGB|0:RAM");
        var copy = LightProfiles.Create(settings, "  Jogo  ");
        Assert.Equal("Jogo", copy.Name);
        Assert.Same(copy, LightProfiles.Active(settings));
        Assert.Equal("Jogo", settings.LightProfileActive);
        Assert.Equal(original.Effect, copy.Effect);
        Assert.Equal((original.R, original.G, original.B), (copy.R, copy.G, copy.B));
        Assert.Equal(original.Brightness, copy.Brightness);
        Assert.Equal(original.Speed, copy.Speed);
        Assert.Equal(original.Exclusive, copy.Exclusive);
        Assert.Equal(original.Unselected, copy.Unselected);
        Assert.NotSame(original.Unselected, copy.Unselected);
        copy.Unselected.Clear();
        Assert.Single(original.Unselected);
    }

    [Fact]
    public void RenomearValidaNomeEAtualizaAtivo()
    {
        var settings = new AppSettings();
        var original = LightProfiles.Active(settings);
        LightProfiles.Rename(settings, original, "  PADRÃO  ");
        Assert.Equal("PADRÃO", settings.LightProfileActive);
        var copy = LightProfiles.Create(settings, "Jogo");
        Assert.Throws<ArgumentException>(() => LightProfiles.Rename(settings, copy, "padrão"));
        LightProfiles.Rename(settings, copy, "  Noite ");
        Assert.Equal("Noite", settings.LightProfileActive);
    }

    [Fact]
    public void ExcluirAtivoEscolheOutroENaoExcluiUltimo()
    {
        var settings = new AppSettings();
        var original = LightProfiles.Active(settings);
        var copy = LightProfiles.Create(settings, "Jogo");
        LightProfiles.Delete(settings, copy);
        Assert.Same(original, LightProfiles.Active(settings));
        Assert.Throws<InvalidOperationException>(() => LightProfiles.Delete(settings, original));
        Assert.Single(settings.LightProfiles);
    }

    [Fact]
    public void SettingsAntigoCarregaPadraoSemAtivarControle()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"LightTheme\":true}")!;
        var profile = LightProfiles.Active(settings);
        Assert.True(settings.LightTheme);
        Assert.Equal("Padrão", profile.Name);
        Assert.Equal("Padrão", settings.LightProfileActive);
        Assert.Equal("static", profile.Effect);
        Assert.Equal((byte)255, profile.R);
        Assert.Equal((byte)255, profile.G);
        Assert.Equal((byte)255, profile.B);
        Assert.Equal(100, profile.Brightness);
        Assert.Equal(5, profile.Speed);
        Assert.True(profile.Exclusive);
        Assert.Empty(profile.Unselected);
    }

    [Fact]
    public void JsonPreservaTodosOsCamposEPerfilAtivo()
    {
        var settings = new AppSettings();
        var profile = LightProfiles.Create(settings, "Noite");
        profile.Effect = "off"; profile.R = 3; profile.G = 2; profile.B = 1;
        profile.Brightness = 25; profile.Speed = 9; profile.Exclusive = false;
        profile.Unselected = new() { "OpenRGB|1:RAM", "Corsair iCUE|abc" };
        string json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(json, JsonSerializer.Serialize(loaded));
        Assert.Equal(JsonSerializer.Serialize(profile), JsonSerializer.Serialize(LightProfiles.Active(loaded)));
    }

    [Fact]
    public void AtivoAusenteEscolhePrimeiroSemCriarOutro()
    {
        var settings = new AppSettings();
        LightProfiles.Create(settings, "Jogo");
        settings.LightProfileActive = "inexistente";
        Assert.Equal("Padrão", LightProfiles.Active(settings).Name);
        Assert.Equal(2, settings.LightProfiles.Count);
    }

    [Fact]
    public void SaveProfileTemCabecalhoETerminadorDoProtocolo()
    {
        byte[] packet = OpenRgbBackend.BuildSaveProfilePacket("ResetExplode");
        Assert.Equal("ORGB", Encoding.ASCII.GetString(packet, 0, 4));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)));
        Assert.Equal(151u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)));
        Assert.Equal(13u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)));
        Assert.Equal(Encoding.ASCII.GetBytes("ResetExplode\0"), packet[16..]);
        Assert.Equal(29, packet.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nome\0outro")]
    [InlineData("iluminação")]
    public void SaveProfileRecusaNomeQueNaoPodeSerEnviado(string name)
    {
        Assert.Throws<ArgumentException>(() => OpenRgbBackend.BuildSaveProfilePacket(name));
    }
}
