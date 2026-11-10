namespace SokagimdakiSorun.Platform.Persistence;

/// <summary>DI'a kaydedilmiş bir modül bağlamı; açılış migration'ı ve tanılama için kullanılır.</summary>
internal sealed record ModuleDbContextRegistration(Type ContextType);
