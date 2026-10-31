// Retries only a local diagnostic file swap. Never retries a business operation.
public static class LocalObservationFile
{
    public static void Publish(string path,string json)
    {
        var tmp=path+".tmp";File.WriteAllText(tmp,json);
        for(var attempt=0;;attempt++)
        {
            try{File.Move(tmp,path,true);return;}
            catch(IOException)when(attempt<20){Thread.Sleep(10);}
            catch(UnauthorizedAccessException)when(attempt<20){Thread.Sleep(10);}
        }
    }
}
