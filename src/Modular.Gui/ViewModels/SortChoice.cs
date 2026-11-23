using Modular.Sdk.Backends;

namespace Modular.Gui.ViewModels;

/// <summary>One entry of a search page's sort list, labelled in the site's own terms.</summary>
public sealed record SortChoice(string Label, ModSortOrder Order)
{
    public override string ToString() => Label;
}
