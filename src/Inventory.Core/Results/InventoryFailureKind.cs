namespace Inventory.Core.Results;

/// <summary>Why the inventory dataset could not be obtained. Deliberately free of transport vocabulary.</summary>
public enum InventoryFailureKind
{
    /// <summary>The source could not be reached or kept failing; trying again later may work.</summary>
    Unavailable,

    /// <summary>The source did not answer within the time budget.</summary>
    Timeout,

    /// <summary>The source refused the request (bad credentials, bad request); retrying will not help.</summary>
    Rejected,

    /// <summary>The source answered, but the data was malformed, incomplete or contradicted itself.</summary>
    InvalidData,
}
