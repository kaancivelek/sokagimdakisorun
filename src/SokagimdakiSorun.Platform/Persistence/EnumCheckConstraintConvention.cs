using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace SokagimdakiSorun.Platform.Persistence;

/// <summary>
/// Metin olarak saklanan her enum kolonu için <c>CHECK (kolon IN ('A', 'B', …))</c> kısıtı ekler
/// (ARCHITECTURE §14: "Enum saklama: metin + CHECK"). Kısıt adı <c>ck_{tablo}_{kolon}</c>.
/// Model sonlandırılırken çalışır; böylece <c>snake_case</c> ile yeniden adlandırılmış kolon adını kullanır.
/// </summary>
internal sealed class EnumCheckConstraintConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (table is null || entityType.IsTableExcludedFromMigrations())
            {
                continue;
            }

            var storeObject = StoreObjectIdentifier.Table(table, entityType.GetSchema());
            foreach (var property in entityType.GetDeclaredProperties())
            {
                var enumType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                // Ön-konvansiyon HaveConversion<string>() sağlayıcı tipini ayarlar; açık bir ValueConverter da olabilir.
                var providerType = property.GetValueConverter()?.ProviderClrType ?? property.GetProviderClrType();
                if (!enumType.IsEnum || providerType != typeof(string))
                {
                    continue;
                }

                var column = property.GetColumnName(storeObject);
                if (column is null)
                {
                    continue;
                }

                var name = string.Create(CultureInfo.InvariantCulture, $"ck_{table}_{column}");
                if (entityType.FindCheckConstraint(name) is not null)
                {
                    continue;
                }

                var values = string.Join(", ", Enum.GetNames(enumType).Select(v => $"'{v.Replace("'", "''", StringComparison.Ordinal)}'"));
                entityType.Builder.HasCheckConstraint(name, $"\"{column}\" IN ({values})");
            }
        }
    }
}
