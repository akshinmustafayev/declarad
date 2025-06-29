https://medium.com/quick-code/execute-powershell-script-in-remote-machine-using-c-38cef87b796a
https://github.com/NaosProject/Naos.WinRM


// can transfer files to AND from the remote server (over WinRM's protocol!).
var localFilePath = @"D:\Temp\BigFileLocal.nupkg";
var fileBytes = File.ReadAllBytes(localFilePath);
var remoteFilePath = @"D:\Temp\BigFileRemote.nupkg";

machineManager.SendFile(remoteFilePath, fileBytes);
var downloadedBytes = machineManager.RetrieveFile(remoteFilePath);

