https://medium.com/quick-code/execute-powershell-script-in-remote-machine-using-c-38cef87b796a
https://github.com/NaosProject/Naos.WinRM
https://www.hanselman.com/blog/how-do-i-find-which-directory-my-net-core-console-application-was-started-in-or-is-running-from
https://github.com/mayuki/Cocona?tab=readme-ov-file#options
https://registry.terraform.io/providers/hashicorp/ad/latest/docs/resources/user
https://learn.microsoft.com/en-us/powershell/module/activedirectory/new-aduser?view=windowsserver2016-ps
https://medium.com/@eduardosilva_94960/mastering-command-line-parsing-in-net-core-with-commandlineparser-c20721100359


// can transfer files to AND from the remote server (over WinRM's protocol!).
var localFilePath = @"D:\Temp\BigFileLocal.nupkg";
var fileBytes = File.ReadAllBytes(localFilePath);
var remoteFilePath = @"D:\Temp\BigFileRemote.nupkg";

machineManager.SendFile(remoteFilePath, fileBytes);
var downloadedBytes = machineManager.RetrieveFile(remoteFilePath);


//
$Parameters = @{
    Name                    = $displayNameTB.Text
    GivenName               = $firstNameTB.Text
    Surname                = $lastNameTB.Text
    SamAccountName          = $adUserTB.Text
    UserPrincipalName       = $adUserTB.Text
    # Path                    = $OrgUnitCB.Text
    AccountPassword         = $ADpasswordBox.Password.ToString()
    Enabled                 = $true
    ChangePasswordAtLogon   = $true

}
New-ADUser @Parameters

