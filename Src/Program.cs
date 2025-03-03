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
using DiscUtils.Streams;
using DiscUtils.VirtualFileSystem;
using DiscUtils.Dokan;
using DokanNet;
using DokanNet.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Numerics;

#pragma warning disable CA1416

namespace WinMnt
{
    [ApiController]
    [Route("api")]
    public class WebAPI : ControllerBase
    {
        private readonly ImageMounter _imageMounter;
        private readonly IHostApplicationLifetime _lifetime;

        public WebAPI(IHostApplicationLifetime lifeTime, ImageMounter imageMounter)
        {
            _imageMounter = imageMounter;
            _lifetime = lifeTime;
        }

        [HttpGet]
        [Route("status")]
        public IActionResult GetStatus()
        {
            return Ok();
        }

        [HttpPost]
        [Route("service")]
        [Consumes("application/json")]
        public IActionResult PostService([FromBody] string requestBody)
        {
            var request = JsonConvert.DeserializeAnonymousType(requestBody, new
            {
                Shutdown = false
            })!;

            if (request.Shutdown) 
            {
                _lifetime.StopApplication();
            }

            return Ok();
        }

        [HttpPost]
        [Route("image/{id}/unmount")]
        [Consumes("application/json")]
        public IActionResult PostUnmount([FromBody] string requestBody, string id)
        {
            var imginfo = _imageMounter.GetImageInfo(id);

            if (imginfo == null)
            {
                return NotFound();
            }

            var request = JsonConvert.DeserializeAnonymousType(requestBody, new
            {
                VolumeIndex = -1
            })!;

            if (request.VolumeIndex < 0)
            {
                _imageMounter.UnmountAllVolumes(id);

                return Ok();
            }

            return _imageMounter.UnmountVolume(id, request.VolumeIndex) ? Ok() : BadRequest();
        }

        [HttpPost]
        [Route("image/{id}/mount")]
        [Consumes("application/json")]
        public IActionResult PostMount([FromBody] string requestBody, string id)
        {
            var imginfo = _imageMounter.GetImageInfo(id);

            if (imginfo == null)
            {
                return NotFound();
            }

            var request = JsonConvert.DeserializeAnonymousType(requestBody, new
            {
                VolumeIndex = -1,
                MountPoint = string.Empty,
                ShowMetaFiles = false,
                ReadOnly = true
            })!;

            if (request.VolumeIndex < 0)
            {
                _imageMounter.MountAllVolumes(id, request.ShowMetaFiles, request.ReadOnly);

                return Ok();
            }

            var result = _imageMounter.MountVolume(id, request.VolumeIndex, 
                request.MountPoint, request.ShowMetaFiles, request.ReadOnly);

            return result ? Ok() : BadRequest();
        }

        [HttpGet]
        [Route("image/{id}/mount")]
        public IActionResult GetMount(string id)
        {
            var imginfo = _imageMounter.GetImageInfo(id);

            if (imginfo == null)
            {
                return NotFound();
            }

            return Ok(imginfo.Volumes);
        }

        [HttpGet]
        [Route("image/{id}/info")]
        public IActionResult GetImageInfo(string id)
        {
            var volmgr = _imageMounter.GetVolumeManager(id);

            if (volmgr == null)
            {
                return NotFound();
            }

            var volumes = volmgr.GetLogicalVolumes();
            var result = new List<dynamic>();

            foreach (var volume in volumes)
            {
                var fsinfo = DiscUtils.FileSystemManager.DetectFileSystems(volume);
                var rootdir = new List<string>();

                if (fsinfo.Count > 0)
                {
                    // Assuming there exist only one filesystem per volume
                    using var fs = fsinfo[0].Open(volume);

                    if (fs is DiscUtils.Ntfs.NtfsFileSystem ntfs)
                    {
                        ntfs.NtfsOptions.HideHiddenFiles = false;
                        ntfs.NtfsOptions.HideSystemFiles = false;
                    }

                    rootdir.AddRange(fs.GetDirectories(fs.Root.FullName).ToArray());
                    rootdir.AddRange(fs.GetFiles(fs.Root.FullName).ToArray());
                }
                
                result.Add(new
                {
                    Volume = volume,
                    Filesystem = fsinfo,
                    RootDir = rootdir
                });
            }

            return Ok(result);
        }

        [HttpPost]
        [Route("image")]
        [Consumes("application/json")]
        public IActionResult PostImage([FromBody] string requestBody)
        {
            var request = JsonConvert.DeserializeAnonymousType(requestBody, new
            {
                FilePath = string.Empty
            })!;

            var id = _imageMounter.Load(request.FilePath);
  
            return Ok(new
            {
                Id = id
            });
        }
    }

    internal class Program
    {
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public class Options
        {
            [Option('d', "debug", Required = false, Default = false, HelpText = "Enable debug mode.")]
            public bool Debug { get; set; }

            [Option('p', "port", Required = false, Default = 5000u, HelpText = "Listening port number for the REST API interface.")]
            public uint Port { get; set; }

            [Option('i', "interface", Required = false, Default = "localhost", HelpText = "Interface address.")]
            public string? Interface { get; set; }
        }

        static void Main(string[] args)
        { 
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.WriteLine("This program currently only supports Windows.");
                return;
            }

            var options = Parser.Default.ParseArguments<Options>(args);
            var builder = WebApplication.CreateBuilder();

            if(options.Value == null)
            {
                return;
            }

            ImageMounter imageMounter = new(options.Value.Debug);

            if (!options.Value.Debug)
            {
                builder.Logging.ClearProviders();
            }

            builder.WebHost.UseKestrel();
            builder.WebHost.UseUrls($"http://{options.Value.Interface}:{options.Value.Port}");

            builder.Services.AddSingleton(imageMounter);
            builder.Services.AddControllers().AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
                options.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
            });

            var app = builder.Build();

            app.MapControllers();
            app.UseRouting();
            app.Run();
        }
    }
}