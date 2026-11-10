using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Platform.Outbox;

/// <summary>
/// Bir integration event'in süreç içi işleyicisi (ARCHITECTURE §4.2). <see cref="OutboxDispatcher"/> tarafından
/// her mesaj için ayrı bir DI scope'unda çağrılır; kayıt:
/// <see cref="OutboxServiceCollectionExtensions.AddIntegrationEventHandler{TEvent, THandler}"/>.
/// </summary>
/// <remarks>
/// <para><b>Sözleşme — handler idempotent olmak zorundadır.</b> Teslim garantisi <i>en az bir kez</i>dir:</para>
/// <list type="bullet">
///   <item>Handler kendi verisini commit ettikten sonra dağıtıcı satırı "işlendi" olarak işaretleyemeden durursa event yeniden gelir.</item>
///   <item>Aynı event'in birden fazla handler'ı varsa biri hata verdiğinde <b>hepsi</b> yeniden çağrılır.</item>
/// </list>
/// <para>Bu yüzden handler <see cref="IIntegrationEvent.EventId"/>'yi idempotency anahtarı olarak kullanır
/// (ör. kendi tablosunda <c>UNIQUE</c> bir <c>dedup_key</c>) ve yalnızca kendi modülünün verisini, kendi
/// transaction'ında günceller. Dış HTTP çağrısı yapmaz; uzun işleri kendi kuyruğuna yazar.</para>
/// <para>Exception fırlatmak "yeniden dene" demektir: satır üstel backoff + jitter ile ertelenir, azami denemede
/// dead-letter olur. <see cref="CancellationToken"/> kapanışta iptal edilir; iptal deneme sayılmaz.</para>
/// </remarks>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}
