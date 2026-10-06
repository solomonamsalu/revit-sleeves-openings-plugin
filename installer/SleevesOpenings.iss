; Inno Setup script for the Sleeves & Openings add-in.
; Build the add-in first (dotnet build -c Release), then compile this file in Inno Setup.
; Output: installer\Output\SleevesOpenings-Setup.exe  (same name every release, so the bucket link never changes)
; Installs into the same folders as install.bat, for each Revit version that is on the machine.

; Version comes from the DLL (src\AssemblyInfo.cs), so there is only one place to change it
#define AppVersion GetVersionNumbersString("..\bin\Release\2024\SleevesOpenings.dll")

[Setup]
; Never change AppId: it is how a newer installer finds and replaces the old one
AppId={{609A79A0-7F79-47BC-B498-AA2F9BCA0255}
AppName=Sleeves & Openings
AppVersion={#AppVersion}
AppVerName=Sleeves & Openings {#AppVersion}
AppPublisher=Sleeves & Openings
DefaultDirName={commonappdata}\Autodesk\Revit\Addins
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=Output
OutputBaseFilename=SleevesOpenings-Setup
UninstallDisplayName=Sleeves & Openings (Revit add-in)
; Revit locks the DLLs while open: ask the user to close it
CloseApplications=yes
RestartApplications=no
Compression=lzma2
SolidCompression=yes

[Files]
; Revit 2024
Source: "..\bin\Release\2024\*"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2024\SleevesOpenings"; Flags: recursesubdirs ignoreversion; Check: RevitInstalled('2024')
Source: "..\SleevesOpenings.addin"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2024"; Flags: ignoreversion; Check: RevitInstalled('2024')
; Revit 2026
Source: "..\bin\Release\2026\*"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026\SleevesOpenings"; Flags: recursesubdirs ignoreversion; Check: RevitInstalled('2026')
Source: "..\SleevesOpenings.addin"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026"; Flags: ignoreversion; Check: RevitInstalled('2026')

[Code]
function RevitInstalled(Year: String): Boolean;
begin
  Result := DirExists(ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\') + Year);
end;

function InitializeSetup(): Boolean;
begin
  Result := RevitInstalled('2024') or RevitInstalled('2026');
  if not Result then
    MsgBox('Revit 2024 or 2026 was not found on this computer.', mbError, MB_OK);
end;
