using PmpStudy.Core.Entities;

namespace PmpStudy.Core.Rules;

/// <summary>
/// Répétition espacée du carnet d'erreurs : revue à J+1, J+3, J+7 puis J+14.
/// Une réponse fausse (ou marquée « doute ») remet le compteur à J+1 ;
/// deux bonnes réponses de suite passent l'entrée en « maîtrisée ».
/// </summary>
public static class SpacedRepetition
{
    public static readonly int[] IntervalsDays = [1, 3, 7, 14];
    public const int CorrectInARowToMaster = 2;

    public static void RecordMiss(ErrorEntry entry, DateOnly today, bool isReview)
    {
        if (isReview) entry.ReviewCount++;
        entry.TimesWrong++;
        entry.IntervalStep = 0;
        entry.ConsecutiveCorrect = 0;
        entry.Status = ErrorStatus.Active;
        entry.NextReview = today.AddDays(IntervalsDays[0]);
    }

    public static void RecordSuccess(ErrorEntry entry, DateOnly today)
    {
        entry.ReviewCount++;
        entry.ConsecutiveCorrect++;
        if (entry.ConsecutiveCorrect >= CorrectInARowToMaster)
        {
            entry.Status = ErrorStatus.Mastered;
            return;
        }
        entry.IntervalStep = Math.Min(entry.IntervalStep + 1, IntervalsDays.Length - 1);
        entry.NextReview = today.AddDays(IntervalsDays[entry.IntervalStep]);
    }

    public static ErrorEntry CreateFromMiss(DateOnly today, DateTime nowUtc) => new()
    {
        Status = ErrorStatus.Active,
        Origin = ErrorOrigin.App,
        TimesWrong = 1,
        IntervalStep = 0,
        NextReview = today.AddDays(IntervalsDays[0]),
        CreatedUtc = nowUtc,
        UpdatedUtc = nowUtc,
    };

    public static bool IsDue(ErrorEntry entry, DateOnly today) =>
        entry.Status == ErrorStatus.Active && entry.NextReview <= today;
}
