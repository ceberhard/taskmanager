using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Services;

/// <summary>How every importer opens the source spreadsheet.</summary>
public static class ImportWorkbook
{
    /// <summary>
    /// Opens the workbook for reading only. FileShare.ReadWrite lets an import run while the
    /// workbook is open in Excel; nothing is ever written back to it.
    /// </summary>
    public static FileStream OpenRead(string workbookPath)
    {
        if (!File.Exists(workbookPath))
        {
            throw new DomainRuleException($"Workbook not found at {workbookPath}.");
        }

        return new FileStream(workbookPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }
}
