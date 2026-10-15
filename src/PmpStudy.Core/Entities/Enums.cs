namespace PmpStudy.Core.Entities;

/// <summary>Domaines de l'ECO PMP 2026.</summary>
public enum Domain
{
    People = 1,
    Process = 2,
    BusinessEnvironment = 3,
}

public enum Approach
{
    Predictive = 1,
    Agile = 2,
    Hybrid = 3,
}

public enum QuestionType
{
    Single = 1,
    Multiple = 2,
}

public enum SessionType
{
    Training = 1,
    Exam = 2,
    ErrorReplay = 3,
}

public enum FeedbackMode
{
    Immediate = 1,
    AtEnd = 2,
}

public enum PlanDayStatus
{
    Todo = 0,
    InProgress = 1,
    Done = 2,
    Skipped = 3,
}

public enum ErrorStatus
{
    Active = 0,
    Mastered = 1,
}

public enum ErrorOrigin
{
    App = 0,
    External = 1,
}
