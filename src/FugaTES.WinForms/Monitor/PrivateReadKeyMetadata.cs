namespace FugaTES.WinForms.Monitor;

public static class PrivateReadKeyMetadata
{
    public static string Classify(Exception error)=>error is UnauthorizedAccessException?"READ_KEY_ACCESS_DENIED":"READ_KEY_UNAVAILABLE";
    // Metadata only; FileInfo.Exists intentionally hides access-denied errors and is not used as proof of absence.
    public static FileInfo Validate(string path)
    {
        try
        {
            var attributes=File.GetAttributes(path);var file=new FileInfo(path);
            if((attributes&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0||file.Length is <1 or >4096)
                throw new PhysicalConnectionException("READ_KEY_UNAVAILABLE");
            return file;
        }
        catch(PhysicalConnectionException){throw;}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException)
        {throw new PhysicalConnectionException(Classify(error));}
    }
}
