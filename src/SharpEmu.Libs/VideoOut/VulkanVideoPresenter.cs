// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.VideoOut;

internal enum GuestDrawKind
{
    None,
    FullscreenBarycentric,
}

internal static unsafe class VulkanVideoPresenter
{
    private static readonly object _gate = new();
    private static Thread? _thread;
    private static Presentation? _latestPresentation;
    private static uint _windowWidth;
    private static uint _windowHeight;
    private static bool _closed;
    private static bool _splashHidden;

    public static void EnsureStarted(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed || _thread is not null)
            {
                return;
            }
        }

        var hasSplash = PngSplashLoader.TryLoad(
            out var splashPixels,
            out var splashWidth,
            out var splashHeight);
        lock (_gate)
        {
            if (_closed || _thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            _latestPresentation ??= _splashHidden
                ? new Presentation(
                    CreateBlackFrame(width, height),
                    width,
                    height,
                    1,
                    GuestDrawKind.None,
                    IsSplash: false)
                : hasSplash
                ? new Presentation(
                    splashPixels,
                    splashWidth,
                    splashHeight,
                    1,
                    GuestDrawKind.None,
                    IsSplash: true)
                : new Presentation(
                    null,
                    width,
                    height,
                    0,
                    GuestDrawKind.None,
                    IsSplash: false);
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "SharpEmu Vulkan VideoOut",
            };
            _thread.Start();
        }
    }

    public static void Submit(byte[] bgraFrame, uint width, uint height)
    {
        if (bgraFrame.Length != checked((int)(width * height * 4)))
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                bgraFrame,
                width,
                height,
                sequence,
                GuestDrawKind.None,
                IsSplash: false);
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "SharpEmu Vulkan VideoOut",
            };
            _thread.Start();
        }
    }

    public static void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height)
    {
        if (drawKind == GuestDrawKind.None || width == 0 || height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed ||
                _latestPresentation is { Pixels: null } latest &&
                latest.DrawKind == drawKind &&
                latest.Width == width &&
                latest.Height == height)
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                null,
                width,
                height,
                sequence,
                drawKind,
                IsSplash: false);
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "SharpEmu Vulkan VideoOut",
            };
            _thread.Start();
        }
    }

    private static void Run()
    {
        uint width;
        uint height;
        lock (_gate)
        {
            width = _windowWidth == 0 ? _latestPresentation?.Width ?? 1280 : _windowWidth;
            height = _windowHeight == 0 ? _latestPresentation?.Height ?? 720 : _windowHeight;
        }

        try
        {
            using var presenter = new Presenter(width, height);
            presenter.Run();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][ERROR] Vulkan VideoOut presenter failed: {exception.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _closed = true;
                _thread = null;
            }
        }
    }

    private static bool TryTakePresentation(long presentedSequence, out Presentation presentation)
    {
        lock (_gate)
        {
            if (_latestPresentation is not { } latest || latest.Sequence == presentedSequence)
            {
                presentation = default;
                return false;
            }

            presentation = latest;
            return true;
        }
    }

    private readonly record struct Presentation(
        byte[]? Pixels,
        uint Width,
        uint Height,
        long Sequence,
        GuestDrawKind DrawKind,
        bool IsSplash);

    private sealed class Presenter : IDisposable
    {
        private const string FullscreenBarycentricVertexSpirv =
            "AwIjBwAAAQALAAgAMgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ACAAAAAAABAAAAG1haW4AAAAADQAAABoAAAApAAAAAwADAAIAAADCAQAABQAEAAQAAABtYWluAAAAAAUABgALAAAAZ2xfUGVyVmVydGV4AAAAAAYABgALAAAAAAAAAGdsX1Bvc2l0aW9uAAYABwALAAAAAQAAAGdsX1BvaW50U2l6ZQAAAAAGAAcACwAAAAIAAABnbF9DbGlwRGlzdGFuY2UABgAHAAsAAAADAAAAZ2xfQ3VsbERpc3RhbmNlAAUAAwANAAAAAAAAAAUABgAaAAAAZ2xfVmVydGV4SW5kZXgAAAUABQAdAAAAaW5kZXhhYmxlAAAABQAFACkAAABiYXJ5Y2VudHJpYwAFAAUALwAAAGluZGV4YWJsZQAAAEcAAwALAAAAAgAAAEgABQALAAAAAAAAAAsAAAAAAAAASAAFAAsAAAABAAAACwAAAAEAAABIAAUACwAAAAIAAAALAAAAAwAAAEgABQALAAAAAwAAAAsAAAAEAAAARwAEABoAAAALAAAAKgAAAEcABAApAAAAHgAAAAAAAAATAAIAAgAAACEAAwADAAAAAgAAABYAAwAGAAAAIAAAABcABAAHAAAABgAAAAQAAAAVAAQACAAAACAAAAAAAAAAKwAEAAgAAAAJAAAAAQAAABwABAAKAAAABgAAAAkAAAAeAAYACwAAAAcAAAAGAAAACgAAAAoAAAAgAAQADAAAAAMAAAALAAAAOwAEAAwAAAANAAAAAwAAABUABAAOAAAAIAAAAAEAAAArAAQADgAAAA8AAAAAAAAAFwAEABAAAAAGAAAAAgAAACsABAAIAAAAEQAAAAMAAAAcAAQAEgAAABAAAAARAAAAKwAEAAYAAAATAAAAAACAvywABQAQAAAAFAAAABMAAAATAAAAKwAEAAYAAAAVAAAAAABAQCwABQAQAAAAFgAAABUAAAATAAAALAAFABAAAAAXAAAAEwAAABUAAAAsAAYAEgAAABgAAAAUAAAAFgAAABcAAAAgAAQAGQAAAAEAAAAOAAAAOwAEABkAAAAaAAAAAQAAACAABAAcAAAABwAAABIAAAAgAAQAHgAAAAcAAAAQAAAAKwAEAAYAAAAhAAAAAAAAACsABAAGAAAAIgAAAAAAgD8gAAQAJgAAAAMAAAAHAAAAIAAEACgAAAADAAAAEAAAADsABAAoAAAAKQAAAAMAAAAsAAUAEAAAACoAAAAiAAAAIQAAACwABQAQAAAAKwAAACEAAAAiAAAALAAFABAAAAAsAAAAIQAAACEAAAAsAAYAEgAAAC0AAAAqAAAAKwAAACwAAAA2AAUAAgAAAAQAAAAAAAAAAwAAAPgAAgAFAAAAOwAEABwAAAAdAAAABwAAADsABAAcAAAALwAAAAcAAAA9AAQADgAAABsAAAAaAAAAPgADAB0AAAAYAAAAQQAFAB4AAAAfAAAAHQAAABsAAAA9AAQAEAAAACAAAAAfAAAAUQAFAAYAAAAjAAAAIAAAAAAAAABRAAUABgAAACQAAAAgAAAAAQAAAFAABwAHAAAAJQAAACMAAAAkAAAAIQAAACIAAABBAAUAJgAAACcAAAANAAAADwAAAD4AAwAnAAAAJQAAAD0ABAAOAAAALgAAABoAAAA+AAMALwAAAC0AAABBAAUAHgAAADAAAAAvAAAALgAAAD0ABAAQAAAAMQAAADAAAAA+AAMAKQAAADEAAAD9AAEAOAABAA==";

        private const string FullscreenBarycentricFragmentSpirv =
            "AwIjBwAAAQALAAgAEgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ABwAEAAAABAAAAG1haW4AAAAACQAAAAwAAAAQAAMABAAAAAcAAAADAAMAAgAAAMIBAAAFAAQABAAAAG1haW4AAAAABQAFAAkAAABvdXRDb2xvcgAAAAAFAAUADAAAAGJhcnljZW50cmljAEcABAAJAAAAHgAAAAAAAABHAAQADAAAAB4AAAAAAAAAEwACAAIAAAAhAAMAAwAAAAIAAAAWAAMABgAAACAAAAAXAAQABwAAAAYAAAAEAAAAIAAEAAgAAAADAAAABwAAADsABAAIAAAACQAAAAMAAAAXAAQACgAAAAYAAAACAAAAIAAEAAsAAAABAAAACgAAADsABAALAAAADAAAAAEAAAArAAQABgAAAA4AAAAAAAAANgAFAAIAAAAEAAAAAAAAAAMAAAD4AAIABQAAAD0ABAAKAAAADQAAAAwAAABRAAUABgAAAA8AAAANAAAAAAAAAFEABQAGAAAAEAAAAA0AAAABAAAAUAAHAAcAAAARAAAADwAAABAAAAAOAAAADgAAAD4AAwAJAAAAEQAAAP0AAQA4AAEA";

        private readonly IWindow _window;
        private Vk _vk = null!;
        private KhrSurface _surfaceApi = null!;
        private KhrSwapchain _swapchainApi = null!;
        private Instance _instance;
        private SurfaceKHR _surface;
        private PhysicalDevice _physicalDevice;
        private Device _device;
        private Queue _queue;
        private uint _queueFamilyIndex;
        private SwapchainKHR _swapchain;
        private Image[] _swapchainImages = [];
        private ImageView[] _swapchainImageViews = [];
        private Framebuffer[] _framebuffers = [];
        private bool[] _imageInitialized = [];
        private Format _swapchainFormat;
        private Extent2D _extent;
        private RenderPass _renderPass;
        private PipelineLayout _pipelineLayout;
        private Pipeline _barycentricPipeline;
        private CommandPool _commandPool;
        private CommandBuffer _commandBuffer;
        private VkSemaphore _imageAvailable;
        private VkSemaphore _renderFinished;
        private VkBuffer _stagingBuffer;
        private DeviceMemory _stagingMemory;
        private ulong _stagingSize;
        private long _presentedSequence;
        private bool _vulkanReady;
        private bool _firstFramePresented;
        private bool _firstGuestDrawPresented;
        private bool _splashPresented;

        public Presenter(uint width, uint height)
        {
            var options = WindowOptions.DefaultVulkan;
            options.Size = new Vector2D<int>((int)width, (int)height);
            options.Title = VideoOutExports.GetWindowTitle();
            options.WindowBorder = WindowBorder.Fixed;
            options.VSync = true;
            options.FramesPerSecond = 60;
            options.UpdatesPerSecond = 60;
            _window = Window.Create(options);
            _window.Load += Initialize;
            _window.Render += Render;
            _window.Closing += DisposeVulkan;
        }

        public void Run() => _window.Run();

        public void Dispose()
        {
            DisposeVulkan();
            _window.Dispose();
        }

        private void Initialize()
        {
            _vk = Vk.GetApi();
            CreateInstance();
            CreateSurface();
            SelectPhysicalDevice();
            CreateDevice();
            CreateSwapchain();
            CreateCommandResources();
            CreateGuestDrawResources();
            _vulkanReady = true;
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan VideoOut ready: {_extent.Width}x{_extent.Height}, format={_swapchainFormat}");
        }

        private void CreateInstance()
        {
            var applicationName = (byte*)SilkMarshal.StringToPtr("SharpEmu");
            try
            {
                var applicationInfo = new ApplicationInfo
                {
                    SType = StructureType.ApplicationInfo,
                    PApplicationName = applicationName,
                    ApplicationVersion = Vk.MakeVersion(0, 0, 1),
                    PEngineName = applicationName,
                    EngineVersion = Vk.MakeVersion(0, 0, 1),
                    ApiVersion = Vk.Version12,
                };

                var extensions = _window.VkSurface!.GetRequiredExtensions(out var extensionCount);
                var createInfo = new InstanceCreateInfo
                {
                    SType = StructureType.InstanceCreateInfo,
                    PApplicationInfo = &applicationInfo,
                    EnabledExtensionCount = extensionCount,
                    PpEnabledExtensionNames = extensions,
                };

                Check(_vk.CreateInstance(&createInfo, null, out _instance), "vkCreateInstance");
                if (!_vk.TryGetInstanceExtension(_instance, out _surfaceApi))
                {
                    throw new InvalidOperationException("VK_KHR_surface is unavailable.");
                }
            }
            finally
            {
                SilkMarshal.Free((nint)applicationName);
            }
        }

        private void CreateSurface()
        {
            var instanceHandle = new VkHandle(_instance.Handle);
            var surfaceHandle = _window.VkSurface!.Create<AllocationCallbacks>(instanceHandle, null);
            _surface = new SurfaceKHR(surfaceHandle.Handle);
        }

        private void SelectPhysicalDevice()
        {
            uint deviceCount = 0;
            Check(_vk.EnumeratePhysicalDevices(_instance, &deviceCount, null), "vkEnumeratePhysicalDevices");
            if (deviceCount == 0)
            {
                throw new InvalidOperationException("No Vulkan physical device was found.");
            }

            var devices = new PhysicalDevice[deviceCount];
            fixed (PhysicalDevice* devicePointer = devices)
            {
                Check(_vk.EnumeratePhysicalDevices(_instance, &deviceCount, devicePointer), "vkEnumeratePhysicalDevices");
            }

            foreach (var device in devices)
            {
                uint queueCount = 0;
                _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueCount, null);
                var queues = new QueueFamilyProperties[queueCount];
                fixed (QueueFamilyProperties* queuePointer = queues)
                {
                    _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueCount, queuePointer);
                }

                for (uint index = 0; index < queueCount; index++)
                {
                    var supportsGraphics = (queues[index].QueueFlags & QueueFlags.GraphicsBit) != 0;
                    _surfaceApi.GetPhysicalDeviceSurfaceSupport(device, index, _surface, out var supportsPresent);
                    if (!supportsGraphics || !supportsPresent)
                    {
                        continue;
                    }

                    _physicalDevice = device;
                    _queueFamilyIndex = index;
                    return;
                }
            }

            throw new InvalidOperationException("No Vulkan graphics/present queue was found.");
        }

        private void CreateDevice()
        {
            var priority = 1.0f;
            var queueInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = _queueFamilyIndex,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };

            var swapchainExtension = (byte*)SilkMarshal.StringToPtr("VK_KHR_swapchain");
            try
            {
                var createInfo = new DeviceCreateInfo
                {
                    SType = StructureType.DeviceCreateInfo,
                    QueueCreateInfoCount = 1,
                    PQueueCreateInfos = &queueInfo,
                    EnabledExtensionCount = 1,
                    PpEnabledExtensionNames = &swapchainExtension,
                };

                Check(_vk.CreateDevice(_physicalDevice, &createInfo, null, out _device), "vkCreateDevice");
            }
            finally
            {
                SilkMarshal.Free((nint)swapchainExtension);
            }

            _vk.GetDeviceQueue(_device, _queueFamilyIndex, 0, out _queue);
            if (!_vk.TryGetDeviceExtension(_instance, _device, out _swapchainApi))
            {
                throw new InvalidOperationException("VK_KHR_swapchain is unavailable.");
            }
        }

        private void CreateSwapchain()
        {
            Check(
                _surfaceApi.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var capabilities),
                "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

            uint formatCount = 0;
            Check(
                _surfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, null),
                "vkGetPhysicalDeviceSurfaceFormatsKHR");
            var formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* formatPointer = formats)
            {
                Check(
                    _surfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, formatPointer),
                    "vkGetPhysicalDeviceSurfaceFormatsKHR");
            }

            var surfaceFormat = ChooseSurfaceFormat(formats);
            _swapchainFormat = surfaceFormat.Format;
            _extent = ChooseExtent(capabilities);
            var imageCount = capabilities.MinImageCount + 1;
            if (capabilities.MaxImageCount != 0)
            {
                imageCount = Math.Min(imageCount, capabilities.MaxImageCount);
            }

            var compositeAlpha = ChooseCompositeAlpha(capabilities.SupportedCompositeAlpha);
            var createInfo = new SwapchainCreateInfoKHR
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = _surface,
                MinImageCount = imageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = _extent,
                ImageArrayLayers = 1,
                ImageUsage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = SharingMode.Exclusive,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = compositeAlpha,
                PresentMode = PresentModeKHR.FifoKhr,
                Clipped = true,
            };

            Check(_swapchainApi.CreateSwapchain(_device, &createInfo, null, out _swapchain), "vkCreateSwapchainKHR");

            uint swapchainImageCount = 0;
            Check(
                _swapchainApi.GetSwapchainImages(_device, _swapchain, &swapchainImageCount, null),
                "vkGetSwapchainImagesKHR");
            _swapchainImages = new Image[swapchainImageCount];
            fixed (Image* imagePointer = _swapchainImages)
            {
                Check(
                    _swapchainApi.GetSwapchainImages(_device, _swapchain, &swapchainImageCount, imagePointer),
                    "vkGetSwapchainImagesKHR");
            }

            _imageInitialized = new bool[swapchainImageCount];
        }

        private void CreateCommandResources()
        {
            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
                QueueFamilyIndex = _queueFamilyIndex,
            };
            Check(_vk.CreateCommandPool(_device, &poolInfo, null, out _commandPool), "vkCreateCommandPool");

            var allocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Check(_vk.AllocateCommandBuffers(_device, &allocateInfo, out _commandBuffer), "vkAllocateCommandBuffers");

            var semaphoreInfo = new SemaphoreCreateInfo
            {
                SType = StructureType.SemaphoreCreateInfo,
            };
            Check(_vk.CreateSemaphore(_device, &semaphoreInfo, null, out _imageAvailable), "vkCreateSemaphore");
            Check(_vk.CreateSemaphore(_device, &semaphoreInfo, null, out _renderFinished), "vkCreateSemaphore");

            CreateStagingBuffer((ulong)_extent.Width * _extent.Height * 4);
        }

        private void CreateGuestDrawResources()
        {
            var colorAttachment = new AttachmentDescription
            {
                Format = _swapchainFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.PresentSrcKhr,
            };
            var colorReference = new AttachmentReference
            {
                Attachment = 0,
                Layout = ImageLayout.ColorAttachmentOptimal,
            };
            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorReference,
            };
            var dependency = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
            };
            var renderPassInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 1,
                PAttachments = &colorAttachment,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };
            Check(_vk.CreateRenderPass(_device, &renderPassInfo, null, out _renderPass), "vkCreateRenderPass");

            _swapchainImageViews = new ImageView[_swapchainImages.Length];
            _framebuffers = new Framebuffer[_swapchainImages.Length];
            for (var index = 0; index < _swapchainImages.Length; index++)
            {
                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = _swapchainImages[index],
                    ViewType = ImageViewType.Type2D,
                    Format = _swapchainFormat,
                    Components = new ComponentMapping(
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity),
                    SubresourceRange = ColorSubresourceRange(),
                };
                Check(
                    _vk.CreateImageView(_device, &viewInfo, null, out _swapchainImageViews[index]),
                    "vkCreateImageView");

                var imageView = _swapchainImageViews[index];
                var framebufferInfo = new FramebufferCreateInfo
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = _renderPass,
                    AttachmentCount = 1,
                    PAttachments = &imageView,
                    Width = _extent.Width,
                    Height = _extent.Height,
                    Layers = 1,
                };
                Check(
                    _vk.CreateFramebuffer(_device, &framebufferInfo, null, out _framebuffers[index]),
                    "vkCreateFramebuffer");
            }

            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
            };
            Check(
                _vk.CreatePipelineLayout(_device, &layoutInfo, null, out _pipelineLayout),
                "vkCreatePipelineLayout");
            CreateBarycentricPipeline();
        }

        private void CreateBarycentricPipeline()
        {
            var vertexBytes = Convert.FromBase64String(FullscreenBarycentricVertexSpirv);
            var fragmentBytes = Convert.FromBase64String(FullscreenBarycentricFragmentSpirv);
            var vertexModule = CreateShaderModule(vertexBytes);
            var fragmentModule = CreateShaderModule(fragmentBytes);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var shaderStages = stackalloc PipelineShaderStageCreateInfo[2];
                shaderStages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = entryPoint,
                };
                shaderStages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragmentModule,
                    PName = entryPoint,
                };

                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList,
                };
                var viewport = new Viewport(0, 0, _extent.Width, _extent.Height, 0, 1);
                var scissor = new Rect2D(new Offset2D(0, 0), _extent);
                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    PViewports = &viewport,
                    ScissorCount = 1,
                    PScissors = &scissor,
                };
                var rasterization = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1,
                };
                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                };
                var colorBlendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask =
                        ColorComponentFlags.RBit |
                        ColorComponentFlags.GBit |
                        ColorComponentFlags.BBit |
                        ColorComponentFlags.ABit,
                };
                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &colorBlendAttachment,
                };
                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = shaderStages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterization,
                    PMultisampleState = &multisample,
                    PColorBlendState = &colorBlend,
                    Layout = _pipelineLayout,
                    RenderPass = _renderPass,
                    Subpass = 0,
                };
                Check(
                    _vk.CreateGraphicsPipelines(
                        _device,
                        default,
                        1,
                        &pipelineInfo,
                        null,
                        out _barycentricPipeline),
                    "vkCreateGraphicsPipelines");
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, fragmentModule, null);
                _vk.DestroyShaderModule(_device, vertexModule, null);
            }
        }

        private ShaderModule CreateShaderModule(byte[] code)
        {
            fixed (byte* codePointer = code)
            {
                var createInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length,
                    PCode = (uint*)codePointer,
                };
                Check(
                    _vk.CreateShaderModule(_device, &createInfo, null, out var module),
                    "vkCreateShaderModule");
                return module;
            }
        }

        private void CreateStagingBuffer(ulong size)
        {
            var bufferInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = BufferUsageFlags.TransferSrcBit,
                SharingMode = SharingMode.Exclusive,
            };
            Check(_vk.CreateBuffer(_device, &bufferInfo, null, out _stagingBuffer), "vkCreateBuffer");

            _vk.GetBufferMemoryRequirements(_device, _stagingBuffer, out var requirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
            };
            Check(_vk.AllocateMemory(_device, &memoryInfo, null, out _stagingMemory), "vkAllocateMemory");
            Check(_vk.BindBufferMemory(_device, _stagingBuffer, _stagingMemory, 0), "vkBindBufferMemory");
            _stagingSize = size;
        }

        private uint FindMemoryType(uint typeBits, MemoryPropertyFlags requiredFlags)
        {
            _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var properties);
            var memoryTypes = &properties.MemoryTypes.Element0;
            for (uint index = 0; index < properties.MemoryTypeCount; index++)
            {
                if ((typeBits & (1u << (int)index)) != 0 &&
                    (memoryTypes[index].PropertyFlags & requiredFlags) == requiredFlags)
                {
                    return index;
                }
            }

            throw new InvalidOperationException("No compatible Vulkan host-visible memory type was found.");
        }

        private void Render(double _)
        {
            if (!_vulkanReady || !TryTakePresentation(_presentedSequence, out var presentation))
            {
                return;
            }

            if (presentation.Pixels is null &&
                presentation.DrawKind != GuestDrawKind.FullscreenBarycentric)
            {
                return;
            }

            byte[]? pixels = null;
            if (presentation.Pixels is { } sourcePixels)
            {
                pixels = presentation.Width == _extent.Width && presentation.Height == _extent.Height
                    ? sourcePixels
                    : ScaleBgra(
                        sourcePixels,
                        presentation.Width,
                        presentation.Height,
                        _extent.Width,
                        _extent.Height);
                if ((ulong)pixels.Length > _stagingSize)
                {
                    return;
                }
            }

            uint imageIndex;
            Check(
                _swapchainApi.AcquireNextImage(
                    _device,
                    _swapchain,
                    ulong.MaxValue,
                    _imageAvailable,
                    default,
                    &imageIndex),
                "vkAcquireNextImageKHR");

            if (pixels is not null)
            {
                void* mapped;
                Check(
                    _vk.MapMemory(_device, _stagingMemory, 0, (ulong)pixels.Length, 0, &mapped),
                    "vkMapMemory");
                fixed (byte* source = pixels)
                {
                    System.Buffer.MemoryCopy(source, mapped, pixels.Length, pixels.Length);
                }
                _vk.UnmapMemory(_device, _stagingMemory);
            }

            Check(_vk.ResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer");
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Check(_vk.BeginCommandBuffer(_commandBuffer, &beginInfo), "vkBeginCommandBuffer");

            PipelineStageFlags waitStage;
            if (pixels is not null)
            {
                RecordUpload(imageIndex);
                waitStage = PipelineStageFlags.TransferBit;
            }
            else if (presentation.DrawKind == GuestDrawKind.FullscreenBarycentric)
            {
                var clearValue = default(ClearValue);
                var renderPassInfo = new RenderPassBeginInfo
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = _renderPass,
                    Framebuffer = _framebuffers[imageIndex],
                    RenderArea = new Rect2D(new Offset2D(0, 0), _extent),
                    ClearValueCount = 1,
                    PClearValues = &clearValue,
                };
                _vk.CmdBeginRenderPass(
                    _commandBuffer,
                    &renderPassInfo,
                    SubpassContents.Inline);
                _vk.CmdBindPipeline(
                    _commandBuffer,
                    PipelineBindPoint.Graphics,
                    _barycentricPipeline);
                _vk.CmdDraw(_commandBuffer, 3, 1, 0, 0);
                _vk.CmdEndRenderPass(_commandBuffer);
                waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unsupported translated guest draw: {presentation.DrawKind}.");
            }

            Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");

            var imageAvailable = _imageAvailable;
            var commandBuffer = _commandBuffer;
            var renderFinished = _renderFinished;
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &imageAvailable,
                PWaitDstStageMask = &waitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &renderFinished,
            };
            Check(_vk.QueueSubmit(_queue, 1, &submitInfo, default), "vkQueueSubmit");

            var swapchain = _swapchain;
            var presentInfo = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &renderFinished,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &imageIndex,
            };
            Check(_swapchainApi.QueuePresent(_queue, &presentInfo), "vkQueuePresentKHR");
            Check(_vk.QueueWaitIdle(_queue), "vkQueueWaitIdle");
            _imageInitialized[imageIndex] = true;
            _presentedSequence = presentation.Sequence;
            if (presentation.IsSplash && !_splashPresented)
            {
                _splashPresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented splash: " +
                    $"{presentation.Width}x{presentation.Height}");
            }
            else if (!presentation.IsSplash && !_firstFramePresented)
            {
                _firstFramePresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented first frame: " +
                    $"{presentation.Width}x{presentation.Height}");
            }

            if (pixels is null && !_firstGuestDrawPresented)
            {
                _firstGuestDrawPresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented translated guest draw: " +
                    $"{presentation.DrawKind}");
            }
        }

        private void RecordUpload(uint imageIndex)
        {
            var oldLayout = _imageInitialized[imageIndex]
                ? ImageLayout.PresentSrcKhr
                : ImageLayout.Undefined;
            var toTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = _imageInitialized[imageIndex] ? AccessFlags.MemoryReadBit : 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = oldLayout,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                _imageInitialized[imageIndex]
                    ? PipelineStageFlags.BottomOfPipeBit
                    : PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toTransfer);

            var copyRegion = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LayerCount = 1,
                },
                ImageExtent = new Extent3D(_extent.Width, _extent.Height, 1),
            };
            _vk.CmdCopyBufferToImage(
                _commandBuffer,
                _stagingBuffer,
                _swapchainImages[imageIndex],
                ImageLayout.TransferDstOptimal,
                1,
                &copyRegion);

            var toPresent = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.MemoryReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.PresentSrcKhr,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.BottomOfPipeBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toPresent);
        }

        private Extent2D ChooseExtent(SurfaceCapabilitiesKHR capabilities)
        {
            if (capabilities.CurrentExtent.Width != uint.MaxValue)
            {
                return capabilities.CurrentExtent;
            }

            var size = _window.FramebufferSize;
            return new Extent2D(
                Math.Clamp((uint)Math.Max(size.X, 1), capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                Math.Clamp((uint)Math.Max(size.Y, 1), capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));
        }

        private static SurfaceFormatKHR ChooseSurfaceFormat(IReadOnlyList<SurfaceFormatKHR> formats)
        {
            foreach (var format in formats)
            {
                if (format.Format is Format.B8G8R8A8Srgb or Format.B8G8R8A8Unorm &&
                    format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                {
                    return format;
                }
            }

            return formats.Count > 0
                ? formats[0]
                : throw new InvalidOperationException("The Vulkan surface exposes no pixel formats.");
        }

        private static CompositeAlphaFlagsKHR ChooseCompositeAlpha(CompositeAlphaFlagsKHR supported)
        {
            foreach (var candidate in new[]
                     {
                         CompositeAlphaFlagsKHR.OpaqueBitKhr,
                         CompositeAlphaFlagsKHR.PreMultipliedBitKhr,
                         CompositeAlphaFlagsKHR.PostMultipliedBitKhr,
                         CompositeAlphaFlagsKHR.InheritBitKhr,
                     })
            {
                if ((supported & candidate) != 0)
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("The Vulkan surface exposes no composite alpha mode.");
        }

        private static ImageSubresourceRange ColorSubresourceRange() =>
            new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1,
            };

        private static byte[] ScaleBgra(byte[] source, uint sourceWidth, uint sourceHeight, uint width, uint height)
        {
            var destination = new byte[checked((int)(width * height * 4))];
            for (uint y = 0; y < height; y++)
            {
                var sourceY = (uint)(((ulong)y * sourceHeight) / height);
                for (uint x = 0; x < width; x++)
                {
                    var sourceX = (uint)(((ulong)x * sourceWidth) / width);
                    var sourceOffset = checked((int)(((ulong)sourceY * sourceWidth + sourceX) * 4));
                    var destinationOffset = checked((int)(((ulong)y * width + x) * 4));
                    source.AsSpan(sourceOffset, 4).CopyTo(destination.AsSpan(destinationOffset, 4));
                }
            }

            return destination;
        }

        private void DisposeVulkan()
        {
            if (!_vulkanReady)
            {
                return;
            }

            _vulkanReady = false;
            _vk.DeviceWaitIdle(_device);
            if (_stagingBuffer.Handle != 0)
            {
                _vk.DestroyBuffer(_device, _stagingBuffer, null);
            }
            if (_stagingMemory.Handle != 0)
            {
                _vk.FreeMemory(_device, _stagingMemory, null);
            }
            if (_imageAvailable.Handle != 0)
            {
                _vk.DestroySemaphore(_device, _imageAvailable, null);
            }
            if (_renderFinished.Handle != 0)
            {
                _vk.DestroySemaphore(_device, _renderFinished, null);
            }
            if (_barycentricPipeline.Handle != 0)
            {
                _vk.DestroyPipeline(_device, _barycentricPipeline, null);
            }
            if (_pipelineLayout.Handle != 0)
            {
                _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
            }
            foreach (var framebuffer in _framebuffers)
            {
                if (framebuffer.Handle != 0)
                {
                    _vk.DestroyFramebuffer(_device, framebuffer, null);
                }
            }
            if (_renderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, _renderPass, null);
            }
            foreach (var imageView in _swapchainImageViews)
            {
                if (imageView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, imageView, null);
                }
            }
            if (_commandPool.Handle != 0)
            {
                _vk.DestroyCommandPool(_device, _commandPool, null);
            }
            if (_swapchain.Handle != 0)
            {
                _swapchainApi.DestroySwapchain(_device, _swapchain, null);
            }
            if (_device.Handle != 0)
            {
                _vk.DestroyDevice(_device, null);
            }
            if (_surface.Handle != 0)
            {
                _surfaceApi.DestroySurface(_instance, _surface, null);
            }
            if (_instance.Handle != 0)
            {
                _vk.DestroyInstance(_instance, null);
            }
        }

        private static void Check(Result result, string operation)
        {
            if (result != Result.Success)
            {
                throw new InvalidOperationException($"{operation} failed with {result}.");
            }
        }
    }
}
