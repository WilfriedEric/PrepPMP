namespace PmpStudy.Web.Model.Rules;

public record ReminderMessage(string Title, string Body, string Url);

/// <summary>Rappel quotidien : quand l'envoyer et quoi dire.</summary>
public static class DailyReminder
{
    public static readonly TimeOnly DefaultTime = new(19, 0);

    /// <summary>Au-delà de ce délai après l'heure prévue (serveur arrêté…), on n'envoie plus le rappel du jour.</summary>
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(3);

    public static bool ShouldSend(bool enabled, TimeOnly time, DateOnly? lastSent, DateTime nowLocal)
    {
        if (!enabled) return false;
        var today = DateOnly.FromDateTime(nowLocal);
        if (lastSent == today) return false;
        var due = today.ToDateTime(time);
        return nowLocal >= due && nowLocal - due < CatchUpWindow;
    }

    /// <summary>Message construit à partir de l'état du jour ; null si tout est déjà fait.</summary>
    public static ReminderMessage? Build(int? pageFrom, int? pageTo, bool pagesDone, int questionGoal, int answered, int errorsDue, int daysToExam)
    {
        var parts = new List<string>();
        if (pageFrom is { } from && pageTo is { } to && !pagesDone) parts.Add($"pages {from} à {to}");
        if (questionGoal > answered) parts.Add($"{questionGoal - answered} question{(questionGoal - answered > 1 ? "s" : "")}");
        if (errorsDue > 0) parts.Add($"{errorsDue} erreur{(errorsDue > 1 ? "s" : "")} à revoir");

        var countdown = daysToExam > 0 ? $"J-{daysToExam} avant l'examen" : daysToExam == 0 ? "Examen aujourd'hui" : "PMP Study";
        if (parts.Count == 0)
            return questionGoal == 0 && pageFrom is null
                ? new ReminderMessage(countdown, "Pas de plan aujourd'hui : une petite série de questions pour garder le rythme ?", "/entrainement")
                : null;

        var url = pageFrom is not null && !pagesDone ? "/" : errorsDue > 0 && questionGoal <= answered ? "/erreurs" : "/";
        return new ReminderMessage(countdown, "Au programme : " + string.Join(", ", parts) + ".", url);
    }
}
