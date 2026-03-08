using System.Collections.Generic;

namespace TrueFluentPro.Models;

public sealed record EndpointInspectionDetails(
    string Title,
    string Intro,
    IReadOnlyList<EndpointInspectionRow> SummaryRows,
    IReadOnlyList<EndpointInspectionSection> Sections,
    string FooterNote);

public sealed record EndpointInspectionSection(
    string Heading,
    IReadOnlyList<EndpointInspectionRow> Rows,
    IReadOnlyList<EndpointInspectionUrlItem> UrlItems);

public sealed record EndpointInspectionRow(
    string Label,
    string Value);

public sealed record EndpointInspectionUrlItem(
    string Label,
    string Url);
