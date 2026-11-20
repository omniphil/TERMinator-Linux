namespace TERMinator.Linux.Models;

public enum TransferStatus
{
    Idle = 0,
    Receiving = 1,
    Sending = 2,
    Complete = 3,
    Error = 4,
    Cancelled = 5
}
