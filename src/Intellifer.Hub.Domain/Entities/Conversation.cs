namespace Intellifer.Hub.Domain.Entities;

public sealed class Conversation
{
    public Guid Id { get; set; }

    public ChannelType Channel { get; set; }

    public string ThreadKey { get; set; } = "";

    public string ParticipantNormalized { get; set; } = "";

    public string? Subject { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastInboundAt { get; set; }

    /// <summary>The Odoo client.contact id of the sender, when one was found.</summary>
    public int? CustomerContactId { get; set; }

    public string? CustomerName { get; set; }

    public string? CustomerCompany { get; set; }

    /// <summary>The Odoo client project(s) of the sender, comma-separated.</summary>
    public string? CustomerProject { get; set; }

    /// <summary>When the customer lookup last ran, whether or not it found anyone.</summary>
    public DateTimeOffset? CustomerCheckedAt { get; set; }

    /// <summary>True once any message in the conversation was flagged as a bug or error. Later messages follow it to Plane.</summary>
    public bool PlaneTracked { get; set; }

    /// <summary>The Plane project the intake item was created in. Follow-up comments go to the same project.</summary>
    public Guid? PlaneProjectId { get; set; }

    /// <summary>Last Plane status the customer was told about, e.g. "1:started". Null until first checked.</summary>
    public string? PlaneStatusKey { get; set; }

    public DateTimeOffset? PlaneStatusCheckedAt { get; set; }

    /// <summary>True once the item is done, cancelled, declined or a duplicate; its status is no longer polled.</summary>
    public bool PlaneClosed { get; set; }

    /// <summary>The Plane intake record created for this conversation.</summary>
    public Guid? PlaneIntakeId { get; set; }

    /// <summary>The Plane work item behind the intake record. Follow-up messages are added to it as comments.</summary>
    public Guid? PlaneWorkItemId { get; set; }

    /// <summary>The work item number shown in Plane, e.g. 124 in SUP-124.</summary>
    public int? PlaneSequenceId { get; set; }

    public List<HubMessage> Messages { get; set; } = [];
}
