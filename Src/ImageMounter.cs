using CommandLine.Text;
using CommandLine;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using DiscUtils.Partitions;
using System.Collections.ObjectModel;
using DiscUtils;
using DiscUtils.Streams;
using DiscUtils.VirtualFileSystem;
using DiscUtils.Dokan;
using DokanNet;
using DokanNet.Logging;
using System.Collections;

#pragma warning disable CA1416

namespace WinMnt;

public class VolumeInfo
{
    public bool IsMounted {  get; set; }

    public string? MountPoint { get; set; }

    public DokanInstance? DokanInstance { get; set; }
}

public class ImageInfo
{
    public VolumeManager? VolumeManager { get; set; }
    public List<VolumeInfo>? Volumes { get; set; }
}

public class ImageMounter
{
    private readonly ConcurrentDictionary<string, ImageInfo> _loadedImages = new();
    private readonly ConsoleLogger? _logger = null;
    private readonly DokanOptions _globalDokanOptions = DokanOptions.FixedDrive;

    public ImageMounter() : this(false)
    {

    }

    public ImageMounter(bool debug)
    {
        // Register all the assemblies (filesystem and containers)
        DiscUtils.Complete.SetupHelper.SetupComplete();

        // Setup debug logs
        if (debug)
        {
            _logger = new ConsoleLogger("[ImageMounter]");
            _globalDokanOptions |= DokanOptions.DebugMode;
        }
        
        // Initialize Dokan2
        Dokan.Init();
    }

    public string? Load(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        using var md5 = MD5.Create();

        string id = BitConverter.ToString(
            md5.ComputeHash(System.Text.Encoding.ASCII.GetBytes(imagePath))
        ).Replace("-", "").ToLowerInvariant();

        // Check if the image has already been loaded
        if (IsLoaded(id))
        {
            return id;
        }

        var disk = VirtualDisk.OpenDisk(imagePath, FileAccess.Read);

        if (disk != null)
        {
            var volmgr = new VolumeManager(disk);
            var volinfo = new List<VolumeInfo>();
            var volumes = volmgr.GetLogicalVolumes();

            foreach (var volume in volumes)
            {
                volinfo.Add(new VolumeInfo 
                { 
                    IsMounted = false, 
                    MountPoint = null
                });
            }

            _loadedImages[id] = new ImageInfo
            {
                VolumeManager = volmgr,
                Volumes = volinfo
            };

            return id;
        }

        return null;
    }

    private bool IsLoaded(string? id)
    {
        if (id != null && _loadedImages.ContainsKey(id))
        {
            return true;
        }

        return false;
    }

    public bool Unload(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        return _loadedImages.Remove(id, out _);
    }

    public void UnloadAll()
    {
        _loadedImages.Clear();
    }

    public ImageInfo? GetImageInfo(string? id)
    {
        if (!IsLoaded(id))
        {
            return null;
        }

        return _loadedImages[id!];
    }

    public VolumeManager? GetVolumeManager(string? id)
    {
        return GetImageInfo(id)?.VolumeManager;
    }

    public static string? GetAvailableDriveLetter()
    {
        var result = Enumerable.Range('D', 'Z' - 'D' + 1).Select(i => (Char)i + ":")
          .Except(DriveInfo.GetDrives().Select(s => s.Name.Replace("\\", "")));

        if (!result.Any())
        {
            return null;
        }

        return result.First() + "\\";
    }

    public bool MountVolume(string id, int volumeIndex, string? mountPoint, bool showMetaFiles, bool readOnly)
    {
        var imginfo = GetImageInfo(id);

        if (imginfo == null || imginfo.Volumes == null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            mountPoint = GetAvailableDriveLetter();

            if (mountPoint == null)
            {
                return false;
            }
        }

        var volumes = imginfo.VolumeManager?.GetLogicalVolumes();

        if (volumes == null || volumeIndex >= volumes.Length || volumeIndex < 0)
        {
            return false;
        }

        var volume = volumes[volumeIndex];
        var fsinfo = DiscUtils.FileSystemManager.DetectFileSystems(volume);

        if (fsinfo == null || fsinfo.Count <= 0)
        {
            return false;
        }

        try
        {
            // Assuming there is only one filesystem per volume
            var file_system = fsinfo[0].Open(volume);

            if (file_system == null)
            {
                return false;
            }

            if (file_system is DiscUtils.Ntfs.NtfsFileSystem ntfs)
            {
                ntfs.NtfsOptions.HideHiddenFiles = false;
                ntfs.NtfsOptions.HideSystemFiles = false;

                if (showMetaFiles)
                {
                    ntfs.NtfsOptions.HideMetafiles = false;
                }
            }

            var dokan_discutils_options = DokanDiscUtilsOptions.HiddenAsNormal;
            var dokan_options = DokanOptions.FixedDrive | _globalDokanOptions;

            if (readOnly)
            {
                dokan_discutils_options |= DokanDiscUtilsOptions.ForceReadOnly;
                dokan_options |= DokanOptions.WriteProtection;
            }

            var dokan_discutils = new DokanDiscUtils(file_system, dokan_discutils_options);

            if (dokan_discutils.NamedStreams)
            {
                dokan_options |= DokanOptions.AltStream;
            }

            imginfo.Volumes[volumeIndex].DokanInstance = dokan_discutils.CreateFileSystem(mountPoint!,
                dokan_options, !file_system.IsThreadSafe, _logger);

            imginfo.Volumes[volumeIndex].IsMounted = true;
            imginfo.Volumes[volumeIndex].MountPoint = mountPoint;

            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
        }

        return false;
    }

    public bool UnmountVolume(string id, int volumeIndex)
    {
        var imginfo = GetImageInfo(id);

        if (imginfo == null || imginfo.Volumes == null)
        {
            return false;
        }

        if (volumeIndex < 0 || imginfo.Volumes.Count <= volumeIndex)
        {
            return false;
        }

        var volinfo = imginfo.Volumes[volumeIndex];

        if ( volinfo == null || 
            volinfo.IsMounted == false ||
            volinfo.DokanInstance == null ||
            volinfo.MountPoint == null )
        {
            return false;
        }

        try
        {
            if (Dokan.IsFileSystemRunning(volinfo.DokanInstance))
            {
                if (Dokan.RemoveMountPoint(volinfo.MountPoint))
                {
                    imginfo.Volumes[volumeIndex].IsMounted = false;
                    imginfo.Volumes[volumeIndex].MountPoint = null;
                    imginfo.Volumes[volumeIndex].DokanInstance = null;

                    return true;
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
        }

        return false;
    }

    public void MountAllVolumes(string id, bool showMetaFiles, bool readOnly)
    {
        var imginfo = GetImageInfo(id);

        if (imginfo == null || imginfo.Volumes == null)
        {
            return;
        }

        for (int i = 0; i < imginfo.Volumes.Count; i++)
        {
            _ = MountVolume(id, i, null, showMetaFiles, readOnly);
        }
    }

    public void UnmountAllVolumes(string id)
    {
        var imginfo = GetImageInfo(id);

        if (imginfo == null || imginfo.Volumes == null)
        {
            return;
        }

        for (int i = 0; i < imginfo.Volumes.Count; i++)
        {
            _ = UnmountVolume(id, i);
        }
    }
}