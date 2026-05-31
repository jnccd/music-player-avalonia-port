using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace MusicPlayerAvaloniaPort.Persistence.Database;

public class NotYetSyncedData(Guid Id, string Endpoint, string Body, string? Error = null, Guid? BelongedToSongId = null)
{
    [Key]
    public Guid Id { get; set; } = Id;
    public string Endpoint { get; set; } = Endpoint;
    public string Body { get; set; } = Body;

    public string? Error { get; set; } = Error;
    public Guid? BelongedToSongId { get; set; } = BelongedToSongId;
}
