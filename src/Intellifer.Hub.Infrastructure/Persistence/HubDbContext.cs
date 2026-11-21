using Intellifer.Hub.Domain;
using Intellifer.Hub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Intellifer.Hub.Infrastructure.Persistence;

public sealed class HubDbContext(DbContextOptions<HubDbContext> options) : DbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<HubMessage> Messages => Set<HubMessage>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    public DbSet<GmailSyncState> GmailSyncStates => Set<GmailSyncState>();

    public DbSet<ChannelHealthRecord> ChannelHealth => Set<ChannelHealthRecord>();

    public DbSet<OutboundMessage> OutboundMessages => Set<OutboundMessage>();

    public DbSet<MessageAttachment> Attachments => Set<MessageAttachment>();

    public DbSet<TeamNotification> TeamNotifications => Set<TeamNotification>();

    public DbSet<AppRegistration> Apps => Set<AppRegistration>();

    public DbSet<AppError> AppErrors => Set<AppError>();

    public DbSet<TeamProject> TeamProjects => Set<TeamProject>();


    public static void Configure(DbContextOptionsBuilder options, string connectionString)
    {
        if (!connectionString.Contains("GSS Encryption Mode", StringComparison.OrdinalIgnoreCase))
            connectionString += ";GSS Encryption Mode=Disable";

        options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(5));
        options.UseSnakeCaseNamingConvention();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.ToTable("conversations");
            entity.HasKey(conversation => conversation.Id);
            entity.Property(conversation => conversation.Id).ValueGeneratedNever();
            entity.Property(conversation => conversation.Channel).HasConversion<string>().HasMaxLength(32);
            entity.Property(conversation => conversation.ThreadKey).HasMaxLength(512);
            entity.Property(conversation => conversation.ParticipantNormalized).HasMaxLength(320);
            entity.Property(conversation => conversation.Subject).HasMaxLength(500);
            entity.Property(conversation => conversation.CustomerName).HasMaxLength(256);
            entity.Property(conversation => conversation.CustomerCompany).HasMaxLength(256);
            entity.Property(conversation => conversation.CustomerProject).HasMaxLength(256);
            entity.HasIndex(conversation => new { conversation.Channel, conversation.ThreadKey })
                .IsUnique()
                .HasDatabaseName("ux_conversations_channel_thread");
        });

        modelBuilder.Entity<HubMessage>(entity =>
        {
            entity.ToTable("messages");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Id).ValueGeneratedNever();
            entity.Property(message => message.Direction).HasConversion<string>().HasMaxLength(32);
            entity.Property(message => message.ExternalMessageId).HasMaxLength(512);
            entity.Property(message => message.SenderRaw).HasMaxLength(320);
            entity.Property(message => message.SenderNormalized).HasMaxLength(320);
            entity.Property(message => message.Subject).HasMaxLength(500);
            entity.Property(message => message.MediaSummary).HasMaxLength(2000);
            entity.Property(message => message.CorrelationId).HasMaxLength(64);
            entity.HasOne(message => message.Conversation)
                .WithMany(conversation => conversation.Messages)
                .HasForeignKey(message => message.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(message => new { message.ConversationId, message.ReceivedAt });
            entity.Property(message => message.PlaneState).HasConversion<string>().HasMaxLength(16);
            entity.Property(message => message.PlaneError).HasMaxLength(1000);
            // The Plane queue only ever scans pending rows.
            entity.HasIndex(message => new { message.PlaneState, message.PlaneNextAttemptAt })
                .HasDatabaseName("ix_messages_plane_queue")
                .HasFilter("plane_state = 'Pending'");
        });

        modelBuilder.Entity<ProcessedMessage>(entity =>
        {
            entity.ToTable("processed_messages");
            entity.HasKey(message => new { message.Channel, message.ExternalMessageId })
                .HasName("pk_processed_messages");
            entity.Property(message => message.Channel).HasConversion<string>().HasMaxLength(32);
            entity.Property(message => message.ExternalMessageId).HasMaxLength(512);
        });

        modelBuilder.Entity<GmailSyncState>(entity =>
        {
            entity.ToTable("gmail_sync_state");
            entity.HasKey(state => state.Mailbox);
            entity.Property(state => state.Mailbox).HasMaxLength(320);
            entity.Property(state => state.HistoryId).HasMaxLength(32);
        });

        modelBuilder.Entity<TeamProject>(entity =>
        {
            entity.ToTable("team_projects");
            entity.HasKey(project => project.Id);
            entity.Property(project => project.Id).ValueGeneratedNever();
            entity.Property(project => project.Name).HasMaxLength(256);
            entity.Property(project => project.Notes).HasMaxLength(2000);
            entity.HasIndex(project => project.Name).IsUnique().HasDatabaseName("ux_team_projects_name");
        });

        modelBuilder.Entity<AppRegistration>(entity =>
        {
            entity.ToTable("apps");
            entity.HasKey(app => app.Id);
            entity.Property(app => app.Id).ValueGeneratedNever();
            entity.Property(app => app.Name).HasMaxLength(100);
            entity.Property(app => app.Project).HasMaxLength(256);
            entity.Property(app => app.KeyHash).HasMaxLength(64);
            entity.Property(app => app.KeyHint).HasMaxLength(32);
            entity.HasIndex(app => app.Name).IsUnique().HasDatabaseName("ux_apps_name");
            entity.HasIndex(app => app.KeyHash).IsUnique().HasDatabaseName("ux_apps_key_hash");
        });

        modelBuilder.Entity<AppError>(entity =>
        {
            entity.ToTable("app_errors");
            entity.HasKey(error => error.Id);
            entity.Property(error => error.Id).ValueGeneratedNever();
            entity.Property(error => error.Source).HasMaxLength(256);
            entity.Property(error => error.Level).HasMaxLength(32);
            entity.Property(error => error.Fingerprint).HasMaxLength(64);
            entity.Property(error => error.PlaneState).HasConversion<string>().HasMaxLength(16);
            entity.Property(error => error.PlaneError).HasMaxLength(1000);
            entity.HasOne(error => error.App)
                .WithMany()
                .HasForeignKey(error => error.AppId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(error => new { error.AppId, error.Fingerprint, error.ReceivedAt })
                .HasDatabaseName("ix_app_errors_repeat");
            entity.HasIndex(error => new { error.PlaneState, error.PlaneNextAttemptAt })
                .HasDatabaseName("ix_app_errors_queue")
                .HasFilter("plane_state = 'Pending'");
        });

        modelBuilder.Entity<TeamNotification>(entity =>
        {
            entity.ToTable("team_notifications");
            entity.HasKey(notice => notice.Id);
            entity.Property(notice => notice.Id).ValueGeneratedNever();
            entity.Property(notice => notice.State).HasConversion<string>().HasMaxLength(16);
            entity.Property(notice => notice.ClientProject).HasMaxLength(256);
            entity.Property(notice => notice.ClientProjectCode).HasMaxLength(64);
            entity.Property(notice => notice.PlaneProject).HasMaxLength(256);
            entity.Property(notice => notice.TicketNumber).HasMaxLength(64);
            entity.Property(notice => notice.Title).HasMaxLength(255);
            entity.Property(notice => notice.Customer).HasMaxLength(255);
            entity.Property(notice => notice.CustomerKey).HasMaxLength(320);
            entity.Property(notice => notice.Note).HasMaxLength(1000);
            entity.HasOne(notice => notice.Conversation)
                .WithMany()
                .HasForeignKey(notice => notice.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(notice => new { notice.State, notice.NextAttemptAt })
                .HasDatabaseName("ix_team_notifications_queue")
                .HasFilter("state = 'Pending'");
        });

        modelBuilder.Entity<MessageAttachment>(entity =>
        {
            entity.ToTable("message_attachments");
            entity.HasKey(attachment => attachment.Id);
            entity.Property(attachment => attachment.Id).ValueGeneratedNever();
            entity.Property(attachment => attachment.FileName).HasMaxLength(255);
            entity.Property(attachment => attachment.ContentType).HasMaxLength(128);
            entity.Property(attachment => attachment.State).HasConversion<string>().HasMaxLength(16);
            entity.Property(attachment => attachment.Error).HasMaxLength(1000);
            entity.HasOne(attachment => attachment.Message)
                .WithMany(message => message.Attachments)
                .HasForeignKey(attachment => attachment.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(attachment => new { attachment.State, attachment.NextAttemptAt })
                .HasDatabaseName("ix_attachments_queue")
                .HasFilter("state IN ('PendingDownload', 'Ready')");
        });

        modelBuilder.Entity<OutboundMessage>(entity =>
        {
            entity.ToTable("outbound_messages");
            entity.HasKey(reply => reply.Id);
            entity.Property(reply => reply.Id).ValueGeneratedNever();
            entity.Property(reply => reply.Channel).HasConversion<string>().HasMaxLength(32);
            entity.Property(reply => reply.State).HasConversion<string>().HasMaxLength(16);
            entity.Property(reply => reply.Recipient).HasMaxLength(512);
            entity.Property(reply => reply.ThreadKey).HasMaxLength(512);
            entity.Property(reply => reply.Subject).HasMaxLength(500);
            entity.Property(reply => reply.Reason).HasMaxLength(128);
            entity.Property(reply => reply.Error).HasMaxLength(1000);
            entity.Property(reply => reply.ExternalMessageId).HasMaxLength(512);
            entity.HasOne(reply => reply.Conversation)
                .WithMany()
                .HasForeignKey(reply => reply.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(reply => new { reply.State, reply.NextAttemptAt })
                .HasDatabaseName("ix_outbound_queue")
                .HasFilter("state = 'Pending'");
        });

        modelBuilder.Entity<Conversation>()
            .Property(conversation => conversation.PlaneStatusKey).HasMaxLength(64);

        modelBuilder.Entity<ChannelHealthRecord>(entity =>
        {
            entity.ToTable("channel_health");
            entity.HasKey(health => health.Channel);
            entity.Property(health => health.Channel).HasConversion<string>().HasMaxLength(32);
            entity.Property(health => health.Status).HasMaxLength(64);
            entity.Property(health => health.Detail).HasMaxLength(500);
        });
    }
}
