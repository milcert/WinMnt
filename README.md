# WinMnt

WinMnt is a command-line interface utility that allows the mounting of image files on Windows via REST API.

- Supported disk files: VHD(X), VMDK, RAW, WIM, XVA
- Supported filesystems: FAT, NTFS, EXT, HFS, SquashFS, XFS, ISO9660, UDF

This project is based on the following open-source libraries:

- https://github.com/LTRData/DiscUtils
- https://github.com/LTRData/dokan-dotnet

# Requirements

- ASP.NET Core 8.0 Runtime or .NET SDK 8.0
	- https://dotnet.microsoft.com/en-us/download/dotnet/8.0

- Dokany (https://github.com/dokan-dev/dokany)
	- E.g. https://github.com/dokan-dev/dokany/releases/download/v2.2.1.1000/Dokan_x64.msi

# Usage example

- Run the binary with the desired parameters (by default it listens on `localhost:5000`)
- Mount a disk via the REST API, for example with Powershell

```powershell
# Helper functions

function WinMnt-Request($method, $endpoint, $body) {
	$uri = "http://localhost:5000/api" + $endpoint
	$header = @{
		"Content-Type" = "application/json"
	}
	if ((!($body -is [string])) -and ($body -ne $null)) {
		$body = "'" + ($body | ConvertTo-Json) + "'"
	}
	return Invoke-RestMethod -Uri $uri -Method $method -Body $body -Headers $header
}

function WinMnt-IsOnline {
	try {
		WinMnt-Request "GET" "/status" $null | Out-Null
		return $true
	} catch {
		return $false
	}
}

# Test connection
if (!(WinMnt-IsOnline)) {
	Write-Host "WinMnt is not yet ready!"
	Exit 1
}

# Load an image
$r = WinMnt-Request "POST" "/image" @{
	FilePath = "Z:\\Disk.vmdk"
}

$id = $r.id
$id

# Get information about the partitions
$info = WinMnt-Request "GET" "/image/$id/info" $null
$info | fl *

# Mount all the volumes (to mount a specific volume set the index)
$r = WinMnt-Request "POST" "/image/$id/mount" @{
	VolumeIndex = -1
	ShowMetaFiles = $true
	ReadOnly = $true
}

# Show mounted volumes
$r = WinMnt-Request "GET" "/image/$id/mount" $null
$r | fl *

# Wait for user input
[System.Console]::ReadKey() | Out-Null

# Unmount volumes (to unmount all, use index -1)
$r = WinMnt-Request "POST" "/image/$id/unmount" @{
	VolumeIndex = -1
}

# Graceful shutdown
$r = WinMnt-Request "POST" "/service" @{
	Shutdown = $true
}
```

# License

MIT License
