SetMetadata("AppName", "Atelier")
SetMetadata("Version", "0.2.45")
SetMetadata("Company", "Fezcode")

SetAppIcon("Assets/atelier-icon.ico")

SetTheme("Windows11")
SetInstallDirSuffix("Atelier")

AddStep("Welcome", { title = "Atelier", description = "This wizard will install Atelier on your computer.\n\nClick Next to continue." })
AddStep("License", { title = "License Agreement", description = "Please review the license terms.", contentFile = "LICENSE.txt", requireScroll = true })
AddStep("Folder", { title = "Select Folder", description = "Choose where to install Atelier." })
AddStep("Shortcuts", {title = "Select Optional Tasks", description = "Check the items you would like the installer to perform." })
AddStep("Install", { title = "Installing...", description = "Copying files to your system." })
AddStep("Finish", { title = "Installation Complete", description = "Atelier has been installed successfully.\n\nClick Finish to close.\n" })

MkDir("%INSTALLDIR%")
CopyDir("Publish", "%INSTALLDIR%/")
-- CopyFiles("builder/DeployPaladin.Builder.pdb", "%INSTALLDIR%/builder/DeployPaladin.Builder.pdb")

CopyFiles("LICENSE.txt", "%INSTALLDIR%/LICENSE.txt")
CopyFiles("README.md", "%INSTALLDIR%/README.md")

-- Desktop and Start Menu shortcuts
CreateShortcut("%INSTALLDIR%/Atelier.exe", "%DESKTOP%", "Atelier", { label = "Create Desktop Shortcut", isOptional = true, isSelected = true })
CreateShortcut("%INSTALLDIR%/Atelier.exe", "%STARTMENU%/Fezcode", "Atelier", { label = "Create Start Menu Entry", isOptional = true, isSelected = true })

CheckRegistry("HKCU", "Software\\Fezcode\\Atelier", "InstallDir")
CreateRegistry("HKCU", "Software\\Fezcode\\Atelier", "InstallDir", "%INSTALLDIR%")
CreateRegistry("HKCU", "Software\\Fezcode\\Atelier", "Version", "0.2.45")
