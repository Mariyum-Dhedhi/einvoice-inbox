namespace EInvoice.Web;

/// <summary>
/// The UI texts: Resources/Texts.resx (English) and Resources/Texts.de.resx (German), read through
/// IStringLocalizer&lt;Texts&gt; (T["Inbox.Title"] in a page). Tests in EInvoice.ComponentTests/Localization fail when a text
/// exists in one language only, when a page writes words of its own, or when a status or rule code has no words.
/// </summary>
public sealed class Texts
{
    private Texts()
    {
    }
}
