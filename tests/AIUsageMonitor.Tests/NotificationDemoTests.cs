using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Notifications;
using AIUsageMonitor.Core.Settings;

namespace AIUsageMonitor.Tests;

public class NotificationDemoTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<SessionChange> AllChanges() =>
        [.. NotificationDemo.FirstWave(Now), .. NotificationDemo.SecondWave(Now)];

    // Le impostazioni di default, come fa NotificationService.ShowDemo: la demo mostra tutto qualunque siano le scelte
    // dell'utente.
    private static NotificationCard Card(SessionChange change) =>
        Assert.IsType<ShowIntent>(NotificationComposer.Compose(change, new AppSettings())).Card;

    [Fact]
    public void First_wave_is_a_finished_turn_and_a_bash_permission()
    {
        var cards = NotificationDemo.FirstWave(Now).Select(Card).ToList();

        Assert.Equal(new[] { NotificationKind.Finished, NotificationKind.Permission }, cards.Select(c => c.Kind));
        Assert.Equal("Finito · 4m 12s", cards[0].Label);
        Assert.Equal("Permesso · Bash", cards[1].Label);
        // Il dettaglio del transcript, non il messaggio generico di permission_prompt.
        Assert.Equal("git push origin main", cards[1].Message);
    }

    [Fact]
    public void Second_wave_is_a_question_and_an_api_error()
    {
        var cards = NotificationDemo.SecondWave(Now).Select(Card).ToList();

        Assert.Equal(new[] { NotificationKind.Question, NotificationKind.Error }, cards.Select(c => c.Kind));
        Assert.Equal("Domanda", cards[0].Label);
        Assert.Equal("Quale database usiamo per i test di integrazione?", cards[0].Message);
        // Sessione dell'app desktop: il titolo porta il prefisso di origine (spec §5.2).
        Assert.StartsWith("app · ", cards[0].Title);
        Assert.Equal("Errore", cards[1].Label);
        Assert.Equal("API Error: 529 overloaded", cards[1].Message);
        Assert.Equal(AgentKind.Codex, cards[1].Agent);
    }

    [Fact]
    public void Every_sample_is_a_card_of_its_own_that_focuses_its_session()
    {
        var cards = AllChanges().Select(Card).ToList();

        // Chiavi diverse: nessuna card d'esempio sostituisce un'altra sul posto.
        Assert.Equal(4, cards.Select(c => c.Key).Distinct().Count());
        Assert.All(cards, c => Assert.Equal(NotificationAction.FocusSession, c.Action));
        Assert.All(cards, c => Assert.NotNull(c.Session));
    }

    [Fact]
    public void Samples_are_synthetic_and_a_click_never_reaches_a_real_terminal_or_a_browser()
    {
        Assert.All(AllChanges(), change =>
        {
            Assert.StartsWith("demo-", change.Session.SessionId);
            Assert.Null(change.Session.Host);
            Assert.Null(change.Session.Cwd);
            Assert.Null(change.Session.TranscriptPath);
            Assert.DoesNotContain(change.Session.Origin, new[] { SessionOrigin.Cloud, SessionOrigin.Routine });
            Assert.False(change.Silent);
        });
    }

    [Fact]
    public void Samples_play_both_sounds_of_the_app()
    {
        // "Finito" il suono morbido, permesso, domanda ed errore quello marcato (spec §8).
        Assert.Equal(new[] { NotificationSoundKind.Done, NotificationSoundKind.Attention },
            NotificationDemo.FirstWave(Now).Select(Card).Select(c => c.Sound));
        Assert.All(NotificationDemo.SecondWave(Now).Select(Card), c => Assert.Equal(NotificationSoundKind.Attention, c.Sound));
    }

    [Fact]
    public void Cards_come_one_sound_apart_and_the_demo_ends_within_a_minute()
    {
        // "Finito" apre la prima ondata insieme all'avviso: entrambi si sono chiusi quando arriva la seconda.
        Assert.True(NotificationDemo.SecondWaveDelay > NotificationComposer.FinishedAutoClose);
        Assert.True(NotificationDemo.SecondWaveDelay > NotificationComposer.InfoAutoClose);
        // Un suono nuovo interrompe quello in corso: tra due card passa piu' del suono piu' lungo (done.wav, 520 ms).
        Assert.True(NotificationDemo.CardInterval > TimeSpan.FromMilliseconds(520));
        // Le ondate non si sovrappongono e l'ultima card arriva ben prima della chiusura forzata.
        Assert.True(NotificationDemo.CardInterval * (NotificationDemo.FirstWave(Now).Count - 1) < NotificationDemo.SecondWaveDelay);
        var lastCard = NotificationDemo.SecondWaveDelay + NotificationDemo.CardInterval * (NotificationDemo.SecondWave(Now).Count - 1);
        Assert.True(lastCard < NotificationDemo.MaxDuration);
        Assert.Equal(TimeSpan.FromSeconds(60), NotificationDemo.MaxDuration);
    }
}
