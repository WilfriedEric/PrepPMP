-- PMP Study : création des tables sur une base SQL Server VIDE.
-- Exécuter le fichier en entier. Les mises à jour suivantes du schéma sont appliquées par l'application au démarrage.

IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
CREATE TABLE [AspNetRoles] (
    [Id] nvarchar(450) NOT NULL,
    [Name] nvarchar(256) NULL,
    [NormalizedName] nvarchar(256) NULL,
    [ConcurrencyStamp] nvarchar(max) NULL,
    CONSTRAINT [PK_AspNetRoles] PRIMARY KEY ([Id])
);

CREATE TABLE [AspNetUsers] (
    [Id] nvarchar(450) NOT NULL,
    [UserName] nvarchar(256) NULL,
    [NormalizedUserName] nvarchar(256) NULL,
    [Email] nvarchar(256) NULL,
    [NormalizedEmail] nvarchar(256) NULL,
    [EmailConfirmed] bit NOT NULL,
    [PasswordHash] nvarchar(max) NULL,
    [SecurityStamp] nvarchar(max) NULL,
    [ConcurrencyStamp] nvarchar(max) NULL,
    [PhoneNumber] nvarchar(max) NULL,
    [PhoneNumberConfirmed] bit NOT NULL,
    [TwoFactorEnabled] bit NOT NULL,
    [LockoutEnd] datetimeoffset NULL,
    [LockoutEnabled] bit NOT NULL,
    [AccessFailedCount] int NOT NULL,
    CONSTRAINT [PK_AspNetUsers] PRIMARY KEY ([Id])
);

CREATE TABLE [Chapters] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(32) NOT NULL,
    [Title] nvarchar(400) NOT NULL,
    [StartPage] int NOT NULL,
    [EndPage] int NOT NULL,
    [ParentId] int NULL,
    [SortOrder] int NOT NULL,
    CONSTRAINT [PK_Chapters] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Chapters_Chapters_ParentId] FOREIGN KEY ([ParentId]) REFERENCES [Chapters] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [CourseFiles] (
    [Id] int NOT NULL IDENTITY,
    [Kind] nvarchar(50) NOT NULL,
    [ContentType] nvarchar(100) NOT NULL,
    [Length] bigint NOT NULL,
    [Content] varbinary(max) NOT NULL,
    [UploadedUtc] datetime2 NOT NULL,
    CONSTRAINT [PK_CourseFiles] PRIMARY KEY ([Id])
);

CREATE TABLE [DataProtectionKeys] (
    [Id] int NOT NULL IDENTITY,
    [FriendlyName] nvarchar(max) NULL,
    [Xml] nvarchar(max) NULL,
    CONSTRAINT [PK_DataProtectionKeys] PRIMARY KEY ([Id])
);

CREATE TABLE [EcoTasks] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(16) NOT NULL,
    [Domain] int NOT NULL,
    [Title] nvarchar(300) NOT NULL,
    CONSTRAINT [PK_EcoTasks] PRIMARY KEY ([Id])
);

CREATE TABLE [PlanDays] (
    [Id] int NOT NULL IDENTITY,
    [Date] date NOT NULL,
    [Week] int NOT NULL,
    [PageFrom] int NULL,
    [PageTo] int NULL,
    [QuestionGoal] int NOT NULL,
    [Label] nvarchar(300) NULL,
    [Status] int NOT NULL,
    CONSTRAINT [PK_PlanDays] PRIMARY KEY ([Id])
);

CREATE TABLE [PushDevices] (
    [Id] int NOT NULL IDENTITY,
    [Endpoint] nvarchar(800) NOT NULL,
    [P256dh] nvarchar(200) NOT NULL,
    [Auth] nvarchar(100) NOT NULL,
    [UserAgent] nvarchar(400) NULL,
    [CreatedUtc] datetime2 NOT NULL,
    [LastSuccessUtc] datetime2 NULL,
    CONSTRAINT [PK_PushDevices] PRIMARY KEY ([Id])
);

CREATE TABLE [Scenarios] (
    [Id] int NOT NULL IDENTITY,
    [Text] nvarchar(max) NOT NULL,
    CONSTRAINT [PK_Scenarios] PRIMARY KEY ([Id])
);

CREATE TABLE [Sessions] (
    [Id] int NOT NULL IDENTITY,
    [Type] int NOT NULL,
    [FeedbackMode] int NOT NULL,
    [StartUtc] datetime2 NOT NULL,
    [DeadlineUtc] datetime2 NOT NULL,
    [EndUtc] datetime2 NULL,
    [DurationSeconds] int NOT NULL,
    [FiltersJson] nvarchar(max) NOT NULL,
    [CorrectCount] int NULL,
    [ScorePercent] float NULL,
    [Title] nvarchar(200) NULL,
    CONSTRAINT [PK_Sessions] PRIMARY KEY ([Id])
);

CREATE TABLE [Settings] (
    [Key] nvarchar(100) NOT NULL,
    [Value] nvarchar(max) NOT NULL,
    CONSTRAINT [PK_Settings] PRIMARY KEY ([Key])
);

CREATE TABLE [AspNetRoleClaims] (
    [Id] int NOT NULL IDENTITY,
    [RoleId] nvarchar(450) NOT NULL,
    [ClaimType] nvarchar(max) NULL,
    [ClaimValue] nvarchar(max) NULL,
    CONSTRAINT [PK_AspNetRoleClaims] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_AspNetRoleClaims_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [AspNetRoles] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [AspNetUserClaims] (
    [Id] int NOT NULL IDENTITY,
    [UserId] nvarchar(450) NOT NULL,
    [ClaimType] nvarchar(max) NULL,
    [ClaimValue] nvarchar(max) NULL,
    CONSTRAINT [PK_AspNetUserClaims] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_AspNetUserClaims_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [AspNetUserLogins] (
    [LoginProvider] nvarchar(450) NOT NULL,
    [ProviderKey] nvarchar(450) NOT NULL,
    [ProviderDisplayName] nvarchar(max) NULL,
    [UserId] nvarchar(450) NOT NULL,
    CONSTRAINT [PK_AspNetUserLogins] PRIMARY KEY ([LoginProvider], [ProviderKey]),
    CONSTRAINT [FK_AspNetUserLogins_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [AspNetUserRoles] (
    [UserId] nvarchar(450) NOT NULL,
    [RoleId] nvarchar(450) NOT NULL,
    CONSTRAINT [PK_AspNetUserRoles] PRIMARY KEY ([UserId], [RoleId]),
    CONSTRAINT [FK_AspNetUserRoles_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [AspNetRoles] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_AspNetUserRoles_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [AspNetUserTokens] (
    [UserId] nvarchar(450) NOT NULL,
    [LoginProvider] nvarchar(450) NOT NULL,
    [Name] nvarchar(450) NOT NULL,
    [Value] nvarchar(max) NULL,
    CONSTRAINT [PK_AspNetUserTokens] PRIMARY KEY ([UserId], [LoginProvider], [Name]),
    CONSTRAINT [FK_AspNetUserTokens_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [Notes] (
    [Id] int NOT NULL IDENTITY,
    [Page] int NULL,
    [Content] nvarchar(max) NOT NULL,
    [Tags] nvarchar(400) NOT NULL,
    [CreatedUtc] datetime2 NOT NULL,
    [UpdatedUtc] datetime2 NOT NULL,
    [ChapterId] int NULL,
    [EcoTaskId] int NULL,
    CONSTRAINT [PK_Notes] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Notes_Chapters_ChapterId] FOREIGN KEY ([ChapterId]) REFERENCES [Chapters] ([Id]) ON DELETE SET NULL,
    CONSTRAINT [FK_Notes_EcoTasks_EcoTaskId] FOREIGN KEY ([EcoTaskId]) REFERENCES [EcoTasks] ([Id]) ON DELETE SET NULL
);

CREATE TABLE [ReadingLogs] (
    [Id] int NOT NULL IDENTITY,
    [Date] date NOT NULL,
    [PageFrom] int NOT NULL,
    [PageTo] int NOT NULL,
    [Minutes] float NOT NULL,
    [StartedUtc] datetime2 NOT NULL,
    [UpdatedUtc] datetime2 NOT NULL,
    [StudyPlanDayId] int NULL,
    CONSTRAINT [PK_ReadingLogs] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ReadingLogs_PlanDays_StudyPlanDayId] FOREIGN KEY ([StudyPlanDayId]) REFERENCES [PlanDays] ([Id]) ON DELETE SET NULL
);

CREATE TABLE [StudyPlanDayChapters] (
    [ChaptersId] int NOT NULL,
    [StudyPlanDayId] int NOT NULL,
    CONSTRAINT [PK_StudyPlanDayChapters] PRIMARY KEY ([ChaptersId], [StudyPlanDayId]),
    CONSTRAINT [FK_StudyPlanDayChapters_Chapters_ChaptersId] FOREIGN KEY ([ChaptersId]) REFERENCES [Chapters] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_StudyPlanDayChapters_PlanDays_StudyPlanDayId] FOREIGN KEY ([StudyPlanDayId]) REFERENCES [PlanDays] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [StudyPlanDayEcoTasks] (
    [EcoTasksId] int NOT NULL,
    [StudyPlanDayId] int NOT NULL,
    CONSTRAINT [PK_StudyPlanDayEcoTasks] PRIMARY KEY ([EcoTasksId], [StudyPlanDayId]),
    CONSTRAINT [FK_StudyPlanDayEcoTasks_EcoTasks_EcoTasksId] FOREIGN KEY ([EcoTasksId]) REFERENCES [EcoTasks] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_StudyPlanDayEcoTasks_PlanDays_StudyPlanDayId] FOREIGN KEY ([StudyPlanDayId]) REFERENCES [PlanDays] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [Questions] (
    [Id] int NOT NULL IDENTITY,
    [ExternalId] nvarchar(100) NULL,
    [Type] int NOT NULL,
    [Stem] nvarchar(max) NOT NULL,
    [Approach] int NOT NULL,
    [Explanation] nvarchar(max) NOT NULL,
    [Source] nvarchar(200) NOT NULL,
    [Difficulty] int NOT NULL,
    [Language] nvarchar(8) NOT NULL,
    [IsActive] bit NOT NULL,
    [EcoTaskId] int NULL,
    [ChapterId] int NULL,
    [ScenarioId] int NULL,
    CONSTRAINT [PK_Questions] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Questions_Chapters_ChapterId] FOREIGN KEY ([ChapterId]) REFERENCES [Chapters] ([Id]) ON DELETE SET NULL,
    CONSTRAINT [FK_Questions_EcoTasks_EcoTaskId] FOREIGN KEY ([EcoTaskId]) REFERENCES [EcoTasks] ([Id]) ON DELETE SET NULL,
    CONSTRAINT [FK_Questions_Scenarios_ScenarioId] FOREIGN KEY ([ScenarioId]) REFERENCES [Scenarios] ([Id]) ON DELETE SET NULL
);

CREATE TABLE [SessionSections] (
    [Id] int NOT NULL IDENTITY,
    [StudySessionId] int NOT NULL,
    [Index] int NOT NULL,
    [StartUtc] datetime2 NULL,
    [EndUtc] datetime2 NULL,
    [BreakStartUtc] datetime2 NULL,
    [BreakEndUtc] datetime2 NULL,
    CONSTRAINT [PK_SessionSections] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_SessionSections_Sessions_StudySessionId] FOREIGN KEY ([StudySessionId]) REFERENCES [Sessions] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [ErrorEntries] (
    [Id] int NOT NULL IDENTITY,
    [QuestionId] int NULL,
    [EcoTaskId] int NULL,
    [Summary] nvarchar(max) NULL,
    [Why] nvarchar(max) NULL,
    [Trap] nvarchar(max) NULL,
    [MindsetReflex] nvarchar(max) NULL,
    [Status] int NOT NULL,
    [Origin] int NOT NULL,
    [ExternalSource] nvarchar(200) NULL,
    [NextReview] date NOT NULL,
    [ReviewCount] int NOT NULL,
    [IntervalStep] int NOT NULL,
    [ConsecutiveCorrect] int NOT NULL,
    [TimesWrong] int NOT NULL,
    [CreatedUtc] datetime2 NOT NULL,
    [UpdatedUtc] datetime2 NOT NULL,
    CONSTRAINT [PK_ErrorEntries] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ErrorEntries_EcoTasks_EcoTaskId] FOREIGN KEY ([EcoTaskId]) REFERENCES [EcoTasks] ([Id]),
    CONSTRAINT [FK_ErrorEntries_Questions_QuestionId] FOREIGN KEY ([QuestionId]) REFERENCES [Questions] ([Id]) ON DELETE SET NULL
);

CREATE TABLE [QuestionOptions] (
    [Id] int NOT NULL IDENTITY,
    [QuestionId] int NOT NULL,
    [Order] int NOT NULL,
    [Text] nvarchar(max) NOT NULL,
    [IsCorrect] bit NOT NULL,
    [MatchKey] nvarchar(100) NULL,
    CONSTRAINT [PK_QuestionOptions] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_QuestionOptions_Questions_QuestionId] FOREIGN KEY ([QuestionId]) REFERENCES [Questions] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [SessionItems] (
    [Id] int NOT NULL IDENTITY,
    [StudySessionId] int NOT NULL,
    [QuestionId] int NOT NULL,
    [Order] int NOT NULL,
    [SectionIndex] int NOT NULL,
    [AnswerJson] nvarchar(max) NULL,
    [AnsweredUtc] datetime2 NULL,
    [IsCorrect] bit NULL,
    [Flagged] bit NOT NULL,
    [Doubt] bit NOT NULL,
    [TimeSeconds] int NOT NULL,
    CONSTRAINT [PK_SessionItems] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_SessionItems_Questions_QuestionId] FOREIGN KEY ([QuestionId]) REFERENCES [Questions] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_SessionItems_Sessions_StudySessionId] FOREIGN KEY ([StudySessionId]) REFERENCES [Sessions] ([Id]) ON DELETE CASCADE
);

CREATE INDEX [IX_AspNetRoleClaims_RoleId] ON [AspNetRoleClaims] ([RoleId]);

CREATE UNIQUE INDEX [RoleNameIndex] ON [AspNetRoles] ([NormalizedName]) WHERE [NormalizedName] IS NOT NULL;

CREATE INDEX [IX_AspNetUserClaims_UserId] ON [AspNetUserClaims] ([UserId]);

CREATE INDEX [IX_AspNetUserLogins_UserId] ON [AspNetUserLogins] ([UserId]);

CREATE INDEX [IX_AspNetUserRoles_RoleId] ON [AspNetUserRoles] ([RoleId]);

CREATE INDEX [EmailIndex] ON [AspNetUsers] ([NormalizedEmail]);

CREATE UNIQUE INDEX [UserNameIndex] ON [AspNetUsers] ([NormalizedUserName]) WHERE [NormalizedUserName] IS NOT NULL;

CREATE UNIQUE INDEX [IX_Chapters_Code] ON [Chapters] ([Code]);

CREATE INDEX [IX_Chapters_ParentId] ON [Chapters] ([ParentId]);

CREATE UNIQUE INDEX [IX_CourseFiles_Kind] ON [CourseFiles] ([Kind]);

CREATE UNIQUE INDEX [IX_EcoTasks_Code] ON [EcoTasks] ([Code]);

CREATE INDEX [IX_ErrorEntries_EcoTaskId] ON [ErrorEntries] ([EcoTaskId]);

CREATE UNIQUE INDEX [IX_ErrorEntries_QuestionId] ON [ErrorEntries] ([QuestionId]) WHERE [QuestionId] IS NOT NULL;

CREATE INDEX [IX_ErrorEntries_Status_NextReview] ON [ErrorEntries] ([Status], [NextReview]);

CREATE INDEX [IX_Notes_ChapterId] ON [Notes] ([ChapterId]);

CREATE INDEX [IX_Notes_EcoTaskId] ON [Notes] ([EcoTaskId]);

CREATE INDEX [IX_Notes_Page] ON [Notes] ([Page]);

CREATE UNIQUE INDEX [IX_PlanDays_Date] ON [PlanDays] ([Date]);

CREATE UNIQUE INDEX [IX_PushDevices_Endpoint] ON [PushDevices] ([Endpoint]);

CREATE INDEX [IX_QuestionOptions_QuestionId] ON [QuestionOptions] ([QuestionId]);

CREATE INDEX [IX_Questions_ChapterId] ON [Questions] ([ChapterId]);

CREATE INDEX [IX_Questions_EcoTaskId] ON [Questions] ([EcoTaskId]);

CREATE UNIQUE INDEX [IX_Questions_ExternalId] ON [Questions] ([ExternalId]) WHERE [ExternalId] IS NOT NULL;

CREATE INDEX [IX_Questions_ScenarioId] ON [Questions] ([ScenarioId]);

CREATE INDEX [IX_ReadingLogs_Date] ON [ReadingLogs] ([Date]);

CREATE INDEX [IX_ReadingLogs_StudyPlanDayId] ON [ReadingLogs] ([StudyPlanDayId]);

CREATE INDEX [IX_SessionItems_AnsweredUtc] ON [SessionItems] ([AnsweredUtc]);

CREATE INDEX [IX_SessionItems_QuestionId] ON [SessionItems] ([QuestionId]);

CREATE UNIQUE INDEX [IX_SessionItems_StudySessionId_Order] ON [SessionItems] ([StudySessionId], [Order]);

CREATE INDEX [IX_Sessions_StartUtc] ON [Sessions] ([StartUtc]);

CREATE UNIQUE INDEX [IX_SessionSections_StudySessionId_Index] ON [SessionSections] ([StudySessionId], [Index]);

CREATE INDEX [IX_StudyPlanDayChapters_StudyPlanDayId] ON [StudyPlanDayChapters] ([StudyPlanDayId]);

CREATE INDEX [IX_StudyPlanDayEcoTasks_StudyPlanDayId] ON [StudyPlanDayEcoTasks] ([StudyPlanDayId]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261016105511_Initial', N'10.0.12');

COMMIT;
GO

BEGIN TRANSACTION;
ALTER TABLE [Sessions] ADD [ClientId] nvarchar(64) NULL;

ALTER TABLE [ReadingLogs] ADD [ClientId] nvarchar(64) NULL;

ALTER TABLE [Notes] ADD [ClientId] nvarchar(64) NULL;

CREATE TABLE [SyncDevices] (
    [Id] int NOT NULL IDENTITY,
    [DeviceId] nvarchar(64) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [UserAgent] nvarchar(400) NULL,
    [CreatedUtc] datetime2 NOT NULL,
    [LastPullUtc] datetime2 NULL,
    [LastPushUtc] datetime2 NULL,
    CONSTRAINT [PK_SyncDevices] PRIMARY KEY ([Id])
);

CREATE UNIQUE INDEX [IX_Sessions_ClientId] ON [Sessions] ([ClientId]) WHERE [ClientId] IS NOT NULL;

CREATE UNIQUE INDEX [IX_ReadingLogs_ClientId] ON [ReadingLogs] ([ClientId]) WHERE [ClientId] IS NOT NULL;

CREATE UNIQUE INDEX [IX_Notes_ClientId] ON [Notes] ([ClientId]) WHERE [ClientId] IS NOT NULL;

CREATE UNIQUE INDEX [IX_SyncDevices_DeviceId] ON [SyncDevices] ([DeviceId]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261016111213_OfflineSync', N'10.0.12');

COMMIT;
GO

