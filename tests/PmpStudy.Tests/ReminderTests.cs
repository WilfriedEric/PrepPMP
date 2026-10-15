using PmpStudy.Core;
using PmpStudy.Core.Rules;

namespace PmpStudy.Tests;

public class DailyReminderRuleTests
{
    private static readonly TimeOnly Seven = new(19, 0);

    [Fact]
    public void Sends_once_after_the_chosen_time_within_the_catch_up_window()
    {
        var day = new DateTime(2026, 11, 2);
        Assert.False(DailyReminder.ShouldSend(true, Seven, null, day.AddHours(18).AddMinutes(59)));
        Assert.True(DailyReminder.ShouldSend(true, Seven, null, day.AddHours(19)));
        Assert.False(DailyReminder.ShouldSend(true, Seven, DateOnly.FromDateTime(day), day.AddHours(19).AddMinutes(5)));
        Assert.False(DailyReminder.ShouldSend(true, Seven, null, day.AddHours(22).AddMinutes(30))); // trop tard
        Assert.False(DailyReminder.ShouldSend(false, Seven, null, day.AddHours(19)));
    }

    [Fact]
    public void Message_lists_what_is_left_and_is_skipped_when_done()
    {
        var m = DailyReminder.Build(12, 30, pagesDone: false, questionGoal: 30, answered: 10, errorsDue: 4, daysToExam: 47);
        Assert.Equal("J-47 avant l'examen", m!.Title);
        Assert.Equal("Au programme : pages 12 à 30, 20 questions, 4 erreurs à revoir.", m.Body);

        Assert.Null(DailyReminder.Build(12, 30, pagesDone: true, questionGoal: 30, answered: 30, errorsDue: 0, daysToExam: 47));
        Assert.Equal("/entrainement", DailyReminder.Build(null, null, false, 0, 0, 0, 10)!.Url);
    }
}

public class NotificationServiceTests
{
    [Fact]
    public async Task Daily_reminder_goes_to_every_device_once_and_drops_expired_ones()
    {
        await using var db = await TestDb.CreateAsync();
        var push = db.Notifications;
        await push.SubscribeAsync("https://push.example/a", "p1", "a1", "phone");
        await push.SubscribeAsync("https://push.example/b", "p2", "a2", "laptop");
        await push.SubscribeAsync("https://push.example/a", "p1-new", "a1-new", "phone"); // réabonnement : pas de doublon
        db.Push.GoneEndpoints.Add("https://push.example/b");
        await push.SaveReminderAsync(true, new TimeOnly(9, 30));
        await db.Content.ImportPlanAsync([new PlanDayImport { Date = db.Clock.Today, Week = 1, PageFrom = 1, PageTo = 10, QuestionGoal = 20 }]);

        // L'horloge de test démarre à 08:00 (UTC = fuseau des tests).
        Assert.False(await push.RunDailyReminderAsync());
        db.Time.Advance(TimeSpan.FromMinutes(90));
        Assert.True(await push.RunDailyReminderAsync());
        Assert.False(await push.RunDailyReminderAsync()); // déjà envoyé aujourd'hui

        var sent = Assert.Single(db.Push.Sent);
        Assert.Equal("https://push.example/a", sent.Endpoint);
        Assert.Contains("pages 1 à 10", sent.Payload);
        Assert.Single(await push.DevicesAsync()); // b expiré et retiré

        db.Time.Advance(TimeSpan.FromDays(1));
        Assert.True(await push.RunDailyReminderAsync()); // lendemain
    }

    [Fact]
    public async Task Vapid_keys_are_generated_once_and_kept()
    {
        await using var db = await TestDb.CreateAsync(seedQuestions: false);
        var first = await db.Notifications.GetVapidKeysAsync();
        var second = await db.Notifications.GetVapidKeysAsync();
        Assert.Equal(first.PublicKey, second.PublicKey);
        Assert.False(string.IsNullOrEmpty(first.PrivateKey));
    }

    [Fact]
    public async Task Rejects_non_https_endpoints()
    {
        await using var db = await TestDb.CreateAsync(seedQuestions: false);
        await Assert.ThrowsAsync<ArgumentException>(() => db.Notifications.SubscribeAsync("http://evil.example/x", "p", "a", null));
    }
}
