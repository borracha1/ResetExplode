namespace ResetExplode;

/// <summary>Valores de iluminação salvos, sem reativar o controle ao abrir o app.</summary>
public sealed class LightProfile
{
    public string Name { get; set; } = "Padrão";
    public string Effect { get; set; } = "static";
    public byte R { get; set; } = 255;
    public byte G { get; set; } = 255;
    public byte B { get; set; } = 255;
    public double Brightness { get; set; } = 100;
    public double Speed { get; set; } = 5;
    public bool Exclusive { get; set; } = true;
    public List<string> Unselected { get; set; } = new();
}

/// <summary>Regras dos perfis, independentes da interface.</summary>
public static class LightProfiles
{
    public static void Ensure(AppSettings settings)
    {
        settings.LightProfiles ??= new();
        if (settings.LightProfiles.Count == 0) settings.LightProfiles.Add(new LightProfile());
        foreach (var profile in settings.LightProfiles) profile.Unselected ??= new();
        settings.LightProfileActive = settings.LightProfiles.FirstOrDefault(p =>
            string.Equals(p.Name, settings.LightProfileActive, StringComparison.OrdinalIgnoreCase))?.Name
            ?? settings.LightProfiles[0].Name;
    }

    public static LightProfile Active(AppSettings settings)
    {
        Ensure(settings);
        return settings.LightProfiles.First(p => p.Name == settings.LightProfileActive);
    }

    public static string ValidateName(AppSettings settings, string name, LightProfile? except = null)
    {
        string clean = name.Trim();
        if (clean.Length == 0) throw new ArgumentException("Digite um nome para o perfil.");
        if (clean.Length > 40) throw new ArgumentException("Use até 40 caracteres no nome.");
        if (settings.LightProfiles.Any(p => p != except && string.Equals(p.Name, clean, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Já existe um perfil com esse nome.");
        return clean;
    }

    public static LightProfile Create(AppSettings settings, string name)
    {
        var current = Active(settings);
        string clean = ValidateName(settings, name);
        var copy = new LightProfile
        {
            Name = clean, Effect = current.Effect, R = current.R, G = current.G, B = current.B,
            Brightness = current.Brightness, Speed = current.Speed, Exclusive = current.Exclusive,
            Unselected = new List<string>(current.Unselected),
        };
        settings.LightProfiles.Add(copy);
        settings.LightProfileActive = copy.Name;
        return copy;
    }

    public static void Rename(AppSettings settings, LightProfile profile, string name)
    {
        bool active = Active(settings) == profile;
        profile.Name = ValidateName(settings, name, profile);
        if (active) settings.LightProfileActive = profile.Name;
    }

    public static void Delete(AppSettings settings, LightProfile profile)
    {
        Ensure(settings);
        if (settings.LightProfiles.Count == 1) throw new InvalidOperationException("O último perfil não pode ser excluído.");
        settings.LightProfiles.Remove(profile);
        Ensure(settings);
    }
}
