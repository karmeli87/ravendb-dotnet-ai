using System.ComponentModel;
using Microsoft.Extensions.VectorData;

// Northwind's own documents (only the fields the deal desk uses).
internal sealed class Company
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Address Address { get; set; } = new();
}

internal sealed class Address
{
    public string Line1 { get; set; } = string.Empty;
    public string? Line2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string? Region { get; set; }
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
}

internal sealed class Employee
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? ReportsTo { get; set; }
}

internal sealed class Category
{
    public string Name { get; set; } = string.Empty;
}

internal sealed class Product
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string QuantityPerUnit { get; set; } = string.Empty;
    public decimal PricePerUnit { get; set; }
    public int UnitsInStock { get; set; }
    public int UnitsOnOrder { get; set; }
    public bool Discontinued { get; set; }
}

internal sealed class Order
{
    public string? Id { get; set; }
    public string Company { get; set; } = string.Empty;
    public string Employee { get; set; } = string.Empty;
    public decimal Freight { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
    public DateTime OrderedAt { get; set; }
    public DateTime RequireAt { get; set; }
    public DateTime? ShippedAt { get; set; }
    public Address ShipTo { get; set; } = new();
    public string ShipVia { get; set; } = string.Empty;
}

internal sealed class OrderLine
{
    public decimal Discount { get; set; }
    public decimal PricePerUnit { get; set; }
    public string Product { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

// The deal: the blackboard every step reads and writes. Revisions are enabled on it, so its history is the negotiation.
internal sealed class DealRequest
{
    public string? Id { get; set; }

    // Not stored: filled from the metadata when the page asks for the deal's history.
    [Newtonsoft.Json.JsonIgnore]
    public DateTime? LastModified { get; set; }

    public string Company { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Status { get; set; } = DealStatus.New;
    public int Round { get; set; }
    public DealAsk? Ask { get; set; }
    public StockCheck? Stock { get; set; }
    public CustomerCheck? Customer { get; set; }
    public MarginCheck? Margin { get; set; }
    public PastDealsCheck? PastDeals { get; set; }
    public List<DebateLine> Debate { get; set; } = [];
    public Ruling? Ruling { get; set; }
    public Escalation? Escalation { get; set; }
    public List<DealMessage> Messages { get; set; } = [];
    public string? OrderId { get; set; }
    public List<string> Log { get; set; } = [];
}

// The subscription picks up New, CustomerReplied and ManagerDecided; every other status is the workflow's own.
internal static class DealStatus
{
    public const string New = "New";
    public const string Reading = "Reading";
    public const string Checking = "Checking";
    public const string Debating = "Debating";
    public const string Deciding = "Deciding";
    public const string WaitingForCustomer = "WaitingForCustomer";
    public const string CustomerReplied = "CustomerReplied";
    public const string WaitingForManager = "WaitingForManager";
    public const string ManagerDecided = "ManagerDecided";
    public const string Won = "Won";
    public const string Lost = "Lost";
    public const string Failed = "Failed";
}

// The deal reader's answer shape (a RavenDB AI agent's sample object), so every field has a typed sample value.
internal sealed class DealAsk
{
    public List<AskedLine> Lines { get; set; } = [];
    public string RequiredBy { get; set; } = string.Empty;
    public decimal Discount { get; set; }
    public string Summary { get; set; } = string.Empty;
}

internal sealed class AskedLine
{
    public string Product { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal PricePerUnit { get; set; }
}

// The four checks.
internal sealed record StockCheck(List<StockLine> Lines);

internal sealed record StockLine(string ProductName, int Quantity, int UnitsInStock, int UnitsOnOrder, bool Short, List<Substitute> Substitutes);

internal sealed record Substitute(string Product, string Name, int UnitsInStock, decimal PricePerUnit);

internal sealed record CustomerCheck(int Orders, decimal Revenue, int Rank, decimal UsualDiscount, DateTime LastOrderAt,
    string Rep, string RepName, string RepTitle, string Manager, string ManagerName, string ManagerTitle, string ShipVia);

internal sealed record MarginCheck(decimal ListTotal, decimal Cost, List<MarginAt> Levels);

internal sealed record MarginAt(decimal Discount, decimal Total, decimal Margin);

internal sealed record PastDealsCheck(List<PastDealHit> Similar);

internal sealed record PastDealHit(string Summary, decimal Discount, string Outcome, double Score);

internal sealed record DebateLine(string Speaker, string Text);

internal sealed record DealMessage(string From, int Round, string Text);

internal sealed record Draft(string DealId, string Text);

internal sealed class Escalation
{
    public string Manager { get; set; } = string.Empty;
    public string ManagerName { get; set; } = string.Empty;
    public string ManagerTitle { get; set; } = string.Empty;
    public decimal Discount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public bool? Approved { get; set; }
    public string? Note { get; set; }
}

// The deal lead's typed ruling (MAF structured output).
internal sealed class Ruling
{
    [Description("accept, counter, escalate or decline")]
    public string Decision { get; set; } = string.Empty;

    [Description("The discount of the terms you rule on, as a fraction (0.12 for 12%)")]
    public decimal Discount { get; set; }

    [Description("The lines, substitutes and delivery in one or two sentences (the discount is its own field)")]
    public string Terms { get; set; } = string.Empty;

    [Description("The pricing policy's discount authority for the title of the customer's sales rep, as a fraction (0.05 for 5%)")]
    public decimal RepAuthority { get; set; }

    [Description("Why, citing the checks, the debate and the pricing policy")]
    public string Reasoning { get; set; } = string.Empty;

    [Description("Who in the debate disagreed, and why; empty when nobody did")]
    public string Dissent { get; set; } = string.Empty;
}

// The debaters' and the writer's answer shape; they are run as plain text.
internal sealed class Statement
{
    public string Text { get; set; } = string.Empty;
}

// Vector collections: RavenDB embeds the text itself, so products and past deals are found by meaning.
internal sealed class ProductCard
{
    [VectorStoreKey]
    public string ProductId { get; set; } = string.Empty;

    [VectorStoreData(IsIndexed = true)]
    public string Category { get; set; } = string.Empty;

    [VectorStoreVector(384)]
    public string Text { get; set; } = string.Empty;
}

internal sealed class PastDeal
{
    [VectorStoreKey]
    public string DealKey { get; set; } = string.Empty;

    [VectorStoreVector(384)]
    public string Summary { get; set; } = string.Empty;

    [VectorStoreData]
    public decimal Discount { get; set; }

    [VectorStoreData]
    public string Outcome { get; set; } = string.Empty;
}

// Messages between the workflow's steps; the data itself is on the deal document.
internal sealed record DealRef(string DealId);

internal sealed record CheckDone(string DealId, string Check);

internal sealed record DealStep(string DealId, string Decision);

internal static class Decisions
{
    public const string Accept = "accept";
    public const string Counter = "counter";
    public const string Escalate = "escalate";
    public const string Decline = "decline";
}
