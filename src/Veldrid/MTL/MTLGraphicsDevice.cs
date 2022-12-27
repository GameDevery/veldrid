using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using NativeLibrary = NativeLibraryLoader.NativeLibrary;
using Veldrid.MetalBindings;
using System.Runtime.Versioning;

namespace Veldrid.MTL
{
    internal unsafe class MTLGraphicsDevice : GraphicsDevice
    {
        private static readonly Lazy<bool> s_isSupported = new Lazy<bool>(GetIsSupported);
        private static readonly Dictionary<IntPtr, MTLGraphicsDevice> s_aotRegisteredBlocks
            = new Dictionary<IntPtr, MTLGraphicsDevice>();

        private readonly MTLDevice _device;
        private readonly string _deviceName;
        private readonly GraphicsApiVersion _apiVersion;
        private readonly MTLCommandQueue _commandQueue;
        private readonly MTLSwapchain _mainSwapchain;
        private readonly bool[] _supportedSampleCounts;
        private BackendInfoMetal _metalInfo;

        private readonly object _submittedCommandsLock = new object();
        private readonly Dictionary<MetalBindings.MTLCommandBuffer, MTLFence> _submittedCBs = new Dictionary<MetalBindings.MTLCommandBuffer, MTLFence>();
        private readonly Dictionary<MetalBindings.MTLCommandBuffer, SmallFixedOrDynamicArray<MTLCommandBuffer>> _submittedCBsMap
            = new Dictionary<MetalBindings.MTLCommandBuffer, SmallFixedOrDynamicArray<MTLCommandBuffer>>();
        private MetalBindings.MTLCommandBuffer _latestSubmittedCB;

        private readonly object _resetEventsLock = new object();
        private readonly List<ManualResetEvent[]> _resetEvents = new List<ManualResetEvent[]>();

        private const string UnalignedBufferCopyPipelineMacOSName = "MTL_UnalignedBufferCopy_macOS";
        private const string UnalignedBufferCopyPipelineiOSName = "MTL_UnalignedBufferCopy_iOS";
        private readonly object _unalignedBufferCopyPipelineLock = new object();
        private readonly NativeLibrary _libSystem;
        private readonly IntPtr _concreteGlobalBlock;
        private MTLShader _unalignedBufferCopyShader;
        private MTLComputePipelineState _unalignedBufferCopyPipeline;
        private MTLCommandBufferHandler _completionHandler;
        private readonly IntPtr _completionHandlerFuncPtr;
        private readonly IntPtr _completionBlockDescriptor;
        private readonly IntPtr _completionBlockLiteral;

        public MTLDevice Device => _device;
        public MTLCommandQueue CommandQueue => _commandQueue;
        public MTLFeatureSupport MetalFeatures { get; }
        public ResourceBindingModel ResourceBindingModel { get; }

        public MTLGraphicsDevice(
            GraphicsDeviceOptions options,
            SwapchainDescription? swapchainDesc)
            : base(ref options)
        {
            _device = MTLDevice.MTLCreateSystemDefaultDevice();
            _deviceName = _device.name;
            MetalFeatures = new MTLFeatureSupport(_device);

            int major = (int)MetalFeatures.MaxFeatureSet / 10000;
            int minor = (int)MetalFeatures.MaxFeatureSet % 10000;
            _apiVersion = new GraphicsApiVersion(major, minor, 0, 0);

            Features = new GraphicsDeviceFeatures(
                computeShader: true,
                geometryShader: false,
                tessellationShaders: false,
                multipleViewports: MetalFeatures.IsSupported(MTLFeatureSet.macOS_GPUFamily1_v3),
                samplerLodBias: false,
                drawBaseVertex: MetalFeatures.IsDrawBaseVertexInstanceSupported(),
                drawBaseInstance: MetalFeatures.IsDrawBaseVertexInstanceSupported(),
                drawIndirect: true,
                drawIndirectBaseInstance: true,
                fillModeWireframe: true,
                samplerAnisotropy: true,
                depthClipDisable: true,
                texture1D: true, // TODO: Should be macOS 10.11+ and iOS 11.0+.
                independentBlend: true,
                structuredBuffer: true,
                subsetTextureView: true,
                commandListDebugMarkers: true,
                bufferRangeBinding: true,
                shaderFloat64: false,
                options.EnableCommandBuffers);
            ResourceBindingModel = options.ResourceBindingModel;

            _libSystem = new NativeLibrary("libSystem.dylib");
            _concreteGlobalBlock = _libSystem.LoadFunction("_NSConcreteGlobalBlock");
            if (MetalFeatures.IsMacOS)
            {
                _completionHandler = OnCommandBufferCompleted;
            }
            else
            {
                _completionHandler = OnCommandBufferCompleted_Static;
            }
            _completionHandlerFuncPtr = Marshal.GetFunctionPointerForDelegate<MTLCommandBufferHandler>(_completionHandler);
            _completionBlockDescriptor = Marshal.AllocHGlobal(Unsafe.SizeOf<BlockDescriptor>());
            BlockDescriptor* descriptorPtr = (BlockDescriptor*)_completionBlockDescriptor;
            descriptorPtr->reserved = 0;
            descriptorPtr->Block_size = (ulong)Unsafe.SizeOf<BlockDescriptor>();

            _completionBlockLiteral = Marshal.AllocHGlobal(Unsafe.SizeOf<BlockLiteral>());
            BlockLiteral* blockPtr = (BlockLiteral*)_completionBlockLiteral;
            blockPtr->isa = _concreteGlobalBlock;
            blockPtr->flags = 1 << 28 | 1 << 29;
            blockPtr->invoke = _completionHandlerFuncPtr;
            blockPtr->descriptor = descriptorPtr;

            if (!MetalFeatures.IsMacOS)
            {
                lock (s_aotRegisteredBlocks)
                {
                    s_aotRegisteredBlocks.Add(_completionBlockLiteral, this);
                }
            }

            ResourceFactory = new MTLResourceFactory(this);
            _commandQueue = _device.newCommandQueue();

            TextureSampleCount[] allSampleCounts = (TextureSampleCount[])Enum.GetValues(typeof(TextureSampleCount));
            _supportedSampleCounts = new bool[allSampleCounts.Length];
            for (int i = 0; i < allSampleCounts.Length; i++)
            {
                TextureSampleCount count = allSampleCounts[i];
                uint uintValue = FormatHelpers.GetSampleCountUInt32(count);
                if (_device.supportsTextureSampleCount((UIntPtr)uintValue))
                {
                    _supportedSampleCounts[i] = true;
                }
            }

            if (swapchainDesc != null)
            {
                SwapchainDescription desc = swapchainDesc.Value;
                _mainSwapchain = new MTLSwapchain(this, ref desc);
            }

            _metalInfo = new BackendInfoMetal(this);

            PostDeviceCreated();
        }

        public override string DeviceName => _deviceName;

        public override string VendorName => "Apple";

        public override GraphicsApiVersion ApiVersion => _apiVersion;

        public override GraphicsBackend BackendType => GraphicsBackend.Metal;

        public override bool IsUvOriginTopLeft => true;

        public override bool IsDepthRangeZeroToOne => true;

        public override bool IsClipSpaceYInverted => false;

        public override ResourceFactory ResourceFactory { get; }

        public override Swapchain MainSwapchain => _mainSwapchain;

        public override GraphicsDeviceFeatures Features { get; }

        private void OnCommandBufferCompleted(IntPtr block, MetalBindings.MTLCommandBuffer cb)
        {
            lock (_submittedCommandsLock)
            {
                if (_submittedCBsMap.TryGetValue(cb, out SmallFixedOrDynamicArray<MTLCommandBuffer> mtlCBs))
                {
                    for (uint i = 0; i < mtlCBs.Count; i++)
                    {
                        mtlCBs.Get(i).ExecutionCompleted();
                    }
                    _submittedCBsMap.Remove(cb);
                }
                if (_submittedCBs.TryGetValue(cb, out MTLFence fence))
                {
                    fence.Set();
                    _submittedCBs.Remove(cb);
                }

                if (_latestSubmittedCB.NativePtr == cb.NativePtr)
                {
                    _latestSubmittedCB = default(MetalBindings.MTLCommandBuffer);
                }
            }

            ObjectiveCRuntime.release(cb.NativePtr);
        }

        // Xamarin AOT requires native callbacks be static.
        // [MonoPInvokeCallback(typeof(MTLCommandBufferHandler))]
        private static void OnCommandBufferCompleted_Static(IntPtr block, MetalBindings.MTLCommandBuffer cb)
        {
            lock (s_aotRegisteredBlocks)
            {
                if (s_aotRegisteredBlocks.TryGetValue(block, out MTLGraphicsDevice gd))
                {
                    gd.OnCommandBufferCompleted(block, cb);
                }
            }
        }

        private protected override void SubmitCommandsCore(CommandList commandList, Fence fence)
        {
            MTLCommandList mtlCL = Util.AssertSubtype<CommandList, MTLCommandList>(commandList);

            mtlCL.CommandBuffer.addCompletedHandler(_completionBlockLiteral);
            lock (_submittedCommandsLock)
            {
                if (fence != null)
                {
                    MTLFence mtlFence = Util.AssertSubtype<Fence, MTLFence>(fence);
                    _submittedCBs.Add(mtlCL.CommandBuffer, mtlFence);
                }

                _latestSubmittedCB = mtlCL.Commit();
            }
        }

        public override TextureSampleCount GetSampleCountLimit(PixelFormat format, bool depthFormat)
        {
            for (int i = _supportedSampleCounts.Length - 1; i >= 0; i--)
            {
                if (_supportedSampleCounts[i])
                {
                    return (TextureSampleCount)i;
                }
            }

            return TextureSampleCount.Count1;
        }

        private protected override bool GetPixelFormatSupportCore(
            PixelFormat format,
            TextureType type,
            TextureUsage usage,
            out PixelFormatProperties properties)
        {
            if (!MTLFormats.IsFormatSupported(format, usage, MetalFeatures))
            {
                properties = default(PixelFormatProperties);
                return false;
            }

            uint sampleCounts = 0;

            for (int i = 0; i < _supportedSampleCounts.Length; i++)
            {
                if (_supportedSampleCounts[i])
                {
                    sampleCounts |= (uint)(1 << i);
                }
            }

            MTLFeatureSet maxFeatureSet = MetalFeatures.MaxFeatureSet;
            uint maxArrayLayer = MTLFormats.GetMaxTextureVolume(maxFeatureSet);
            uint maxWidth;
            uint maxHeight;
            uint maxDepth;
            if (type == TextureType.Texture1D)
            {
                maxWidth = MTLFormats.GetMaxTexture1DWidth(maxFeatureSet);
                maxHeight = 1;
                maxDepth = 1;
            }
            else if (type == TextureType.Texture2D)
            {
                uint maxDimensions;
                if ((usage & TextureUsage.Cubemap) != 0)
                {
                    maxDimensions = MTLFormats.GetMaxTextureCubeDimensions(maxFeatureSet);
                }
                else
                {
                    maxDimensions = MTLFormats.GetMaxTexture2DDimensions(maxFeatureSet);
                }

                maxWidth = maxDimensions;
                maxHeight = maxDimensions;
                maxDepth = 1;
            }
            else if (type == TextureType.Texture3D)
            {
                maxWidth = maxArrayLayer;
                maxHeight = maxArrayLayer;
                maxDepth = maxArrayLayer;
                maxArrayLayer = 1;
            }
            else
            {
                throw Illegal.Value<TextureType>();
            }

            properties = new PixelFormatProperties(
                maxWidth,
                maxHeight,
                maxDepth,
                uint.MaxValue,
                maxArrayLayer,
                sampleCounts);
            return true;
        }

        private protected override void SwapBuffersCore(Swapchain swapchain)
        {
            MTLSwapchain mtlSC = Util.AssertSubtype<Swapchain, MTLSwapchain>(swapchain);
            MTLSwapchainFramebuffer mtlSCFB = Util.AssertSubtype<Framebuffer, MTLSwapchainFramebuffer>(
                mtlSC.Framebuffers[mtlSC.ImageIndex]);
            var currentDrawablePtr = mtlSCFB.Drawable.NativePtr;
            if (currentDrawablePtr != IntPtr.Zero)
            {
                using (NSAutoreleasePool.Begin())
                {
                    MetalBindings.MTLCommandBuffer submitCB = _commandQueue.commandBuffer();
                    submitCB.presentDrawable(currentDrawablePtr);
                    submitCB.commit();
                }
            }

            AcquireNextImageCore(swapchain, null, null, out uint imageIndex);
        }

        private protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
        {
            var mtlBuffer = Util.AssertSubtype<DeviceBuffer, MTLBuffer>(buffer);
            void* destPtr = mtlBuffer.DeviceBuffer.contents();
            byte* destOffsetPtr = (byte*)destPtr + bufferOffsetInBytes;
            Unsafe.CopyBlock(destOffsetPtr, source.ToPointer(), sizeInBytes);
        }

        private protected override void UpdateTextureCore(
            Texture texture,
            IntPtr source,
            uint sizeInBytes,
            uint x,
            uint y,
            uint z,
            uint width,
            uint height,
            uint depth,
            uint mipLevel,
            uint arrayLayer)
        {
            MTLTexture mtlTex = Util.AssertSubtype<Texture, MTLTexture>(texture);
            if (mtlTex.StagingBuffer.IsNull)
            {
                Texture stagingTex = ResourceFactory.CreateTexture(new TextureDescription(
                    width, height, depth, 1, 1, texture.Format, TextureUsage.Staging, texture.Type));
                UpdateTexture(stagingTex, source, sizeInBytes, 0, 0, 0, width, height, depth, 0, 0);
                CommandList cl = ResourceFactory.CreateCommandList();
                cl.Begin();
                cl.CopyTexture(
                    stagingTex, 0, 0, 0, 0, 0,
                    texture, x, y, z, mipLevel, arrayLayer,
                    width, height, depth, 1);
                cl.End();
                SubmitCommands(cl);

                cl.Dispose();
                stagingTex.Dispose();
            }
            else
            {
                mtlTex.GetSubresourceLayout(mipLevel, arrayLayer, out uint dstRowPitch, out uint dstDepthPitch);
                ulong dstOffset = Util.ComputeSubresourceOffset(mtlTex, mipLevel, arrayLayer);
                uint srcRowPitch = FormatHelpers.GetRowPitch(width, texture.Format);
                uint srcDepthPitch = FormatHelpers.GetDepthPitch(srcRowPitch, height, texture.Format);
                Util.CopyTextureRegion(
                    source.ToPointer(),
                    0, 0, 0,
                    srcRowPitch, srcDepthPitch,
                    (byte*)mtlTex.StagingBuffer.contents() + dstOffset,
                    x, y, z,
                    dstRowPitch, dstDepthPitch,
                    width, height, depth,
                    texture.Format);
            }
        }

        private protected override void WaitForIdleCore()
        {
            MetalBindings.MTLCommandBuffer lastCB = default;
            lock (_submittedCommandsLock)
            {
                lastCB = _latestSubmittedCB;
                ObjectiveCRuntime.retain(lastCB.NativePtr);
            }

            if (lastCB.NativePtr != IntPtr.Zero && lastCB.status != MTLCommandBufferStatus.Completed)
            {
                lastCB.waitUntilCompleted();
            }

            ObjectiveCRuntime.release(lastCB.NativePtr);
        }

        protected override MappedResource MapCore(MappableResource resource, MapMode mode, uint subresource)
        {
            if (resource is MTLBuffer buffer)
            {
                return MapBuffer(buffer, mode);
            }
            else
            {
                MTLTexture texture = Util.AssertSubtype<MappableResource, MTLTexture>(resource);
                return MapTexture(texture, mode, subresource);
            }
        }

        private MappedResource MapBuffer(MTLBuffer buffer, MapMode mode)
        {
            void* data = buffer.DeviceBuffer.contents();
            return new MappedResource(
                buffer,
                mode,
                (IntPtr)data,
                buffer.SizeInBytes,
                0,
                buffer.SizeInBytes,
                buffer.SizeInBytes);
        }

        private MappedResource MapTexture(MTLTexture texture, MapMode mode, uint subresource)
        {
            Debug.Assert(!texture.StagingBuffer.IsNull);
            void* data = texture.StagingBuffer.contents();
            Util.GetMipLevelAndArrayLayer(texture, subresource, out uint mipLevel, out uint arrayLayer);
            Util.GetMipDimensions(texture, mipLevel, out uint width, out uint height, out uint depth);
            uint subresourceSize = texture.GetSubresourceSize(mipLevel, arrayLayer);
            texture.GetSubresourceLayout(mipLevel, arrayLayer, out uint rowPitch, out uint depthPitch);
            ulong offset = Util.ComputeSubresourceOffset(texture, mipLevel, arrayLayer);
            byte* offsetPtr = (byte*)data + offset;
            return new MappedResource(texture, mode, (IntPtr)offsetPtr, subresourceSize, subresource, rowPitch, depthPitch);
        }

        protected override void PlatformDispose()
        {
            WaitForIdle();
            if (!_unalignedBufferCopyPipeline.IsNull)
            {
                _unalignedBufferCopyShader.Dispose();
                ObjectiveCRuntime.release(_unalignedBufferCopyPipeline.NativePtr);
            }
            _mainSwapchain?.Dispose();
            ObjectiveCRuntime.release(_commandQueue.NativePtr);
            ObjectiveCRuntime.release(_device.NativePtr);

            lock (s_aotRegisteredBlocks)
            {
                s_aotRegisteredBlocks.Remove(_completionBlockLiteral);
            }

            _libSystem.Dispose();
            Marshal.FreeHGlobal(_completionBlockDescriptor);
            Marshal.FreeHGlobal(_completionBlockLiteral);
        }

        public override bool GetMetalInfo(out BackendInfoMetal info)
        {
            info = _metalInfo;
            return true;
        }

        protected override void UnmapCore(MappableResource resource, uint subresource)
        {
        }

        public override bool WaitForFence(Fence fence, ulong nanosecondTimeout)
        {
            return Util.AssertSubtype<Fence, MTLFence>(fence).Wait(nanosecondTimeout);
        }

        public override bool WaitForFences(Fence[] fences, bool waitAll, ulong nanosecondTimeout)
        {
            int msTimeout;
            if (nanosecondTimeout == ulong.MaxValue)
            {
                msTimeout = -1;
            }
            else
            {
                msTimeout = (int)Math.Min(nanosecondTimeout / 1_000_000, int.MaxValue);
            }

            ManualResetEvent[] events = GetResetEventArray(fences.Length);
            for (int i = 0; i < fences.Length; i++)
            {
                events[i] = Util.AssertSubtype<Fence, MTLFence>(fences[i]).ResetEvent;
            }
            bool result;
            if (waitAll)
            {
                result = WaitHandle.WaitAll(events, msTimeout);
            }
            else
            {
                int index = WaitHandle.WaitAny(events, msTimeout);
                result = index != WaitHandle.WaitTimeout;
            }

            ReturnResetEventArray(events);

            return result;
        }

        private ManualResetEvent[] GetResetEventArray(int length)
        {
            lock (_resetEventsLock)
            {
                for (int i = _resetEvents.Count - 1; i > 0; i--)
                {
                    ManualResetEvent[] array = _resetEvents[i];
                    if (array.Length == length)
                    {
                        _resetEvents.RemoveAt(i);
                        return array;
                    }
                }
            }

            ManualResetEvent[] newArray = new ManualResetEvent[length];
            return newArray;
        }

        private void ReturnResetEventArray(ManualResetEvent[] array)
        {
            lock (_resetEventsLock)
            {
                _resetEvents.Add(array);
            }
        }

        public override void ResetFence(Fence fence)
        {
            Util.AssertSubtype<Fence, MTLFence>(fence).Reset();
        }

        internal static bool IsSupported() => s_isSupported.Value;

        private static bool GetIsSupported()
        {
            bool result = false;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    if (RuntimeInformation.OSDescription.Contains("Darwin"))
                    {
                        NSArray allDevices = MTLDevice.MTLCopyAllDevices();
                        result |= (ulong)allDevices.count > 0;
                        ObjectiveCRuntime.release(allDevices.NativePtr);
                    }
                    else
                    {
                        MTLDevice defaultDevice = MTLDevice.MTLCreateSystemDefaultDevice();
                        if (defaultDevice.NativePtr != IntPtr.Zero)
                        {
                            result = true;
                            ObjectiveCRuntime.release(defaultDevice.NativePtr);
                        }
                    }
                }
            }
            catch
            {
                result = false;
            }

            return result;
        }

        internal MTLComputePipelineState GetUnalignedBufferCopyPipeline()
        {
            lock (_unalignedBufferCopyPipelineLock)
            {
                if (_unalignedBufferCopyPipeline.IsNull)
                {
                    MTLComputePipelineDescriptor descriptor = MTLUtil.AllocInit<MTLComputePipelineDescriptor>(
                       nameof(MTLComputePipelineDescriptor));
                    MTLPipelineBufferDescriptor buffer0 = descriptor.buffers[0];
                    buffer0.mutability = MTLMutability.Mutable;
                    MTLPipelineBufferDescriptor buffer1 = descriptor.buffers[1];
                    buffer0.mutability = MTLMutability.Mutable;

                    Debug.Assert(_unalignedBufferCopyShader == null);
                    string name = MetalFeatures.IsMacOS ? UnalignedBufferCopyPipelineMacOSName : UnalignedBufferCopyPipelineiOSName;
                    using (Stream resourceStream = typeof(MTLGraphicsDevice).Assembly.GetManifestResourceStream(name))
                    {
                        byte[] data = new byte[resourceStream.Length];
                        using (MemoryStream ms = new MemoryStream(data))
                        {
                            resourceStream.CopyTo(ms);
                            ShaderDescription shaderDesc = new ShaderDescription(ShaderStages.Compute, data, "copy_bytes");
                            _unalignedBufferCopyShader = new MTLShader(ref shaderDesc, this);
                        }
                    }

                    descriptor.computeFunction = _unalignedBufferCopyShader.Function;
                    _unalignedBufferCopyPipeline = _device.newComputePipelineStateWithDescriptor(descriptor);
                    ObjectiveCRuntime.release(descriptor.NativePtr);
                }

                return _unalignedBufferCopyPipeline;
            }
        }

        internal override uint GetUniformBufferMinOffsetAlignmentCore() => MetalFeatures.IsMacOS ? 16u : 256u;
        internal override uint GetStructuredBufferMinOffsetAlignmentCore() => 16u;

        private protected override void SubmitCommandsCore(
            CommandBuffer commandBuffer,
            Semaphore wait,
            Semaphore signal,
            Fence fence)
        {
            MTLCommandBuffer mtlCB = Util.AssertSubtype<CommandBuffer, MTLCommandBuffer>(commandBuffer);

            lock (_submittedCommandsLock)
            {
                var submissionCB = mtlCB.PrepareForSubmission();
                SmallFixedOrDynamicArray<MTLCommandBuffer> arr = new SmallFixedOrDynamicArray<MTLCommandBuffer>(mtlCB);
                _submittedCBsMap.Add(submissionCB, arr);

                if (fence != null)
                {
                    MTLFence mtlFence = Util.AssertSubtype<Fence, MTLFence>(fence);
                    _submittedCBs.Add(submissionCB, mtlFence);
                }

                submissionCB.addCompletedHandler(_completionBlockLiteral);
                submissionCB.commit();
            }
        }

        private protected override void SubmitCommandsCore(CommandBuffer[] commandBuffers, Semaphore[] waits, Semaphore[] signals, Fence fence)
        {
            if (commandBuffers.Length > 0)
            {
                SmallFixedOrDynamicArray<MTLCommandBuffer> arr
                            = new SmallFixedOrDynamicArray<MTLCommandBuffer>((uint)commandBuffers.Length);

                for (int i = 0; i < commandBuffers.Length - 1; i++)
                {
                    MTLCommandBuffer mtlCB = commandBuffers[i] as MTLCommandBuffer;
                    if (mtlCB == null)
                    {
                        MTLReusableCommandBuffer reusableCB
                            = Util.AssertSubtype<CommandBuffer, MTLReusableCommandBuffer>(commandBuffers[i]);
                        mtlCB = reusableCB.RecordAndGetCommandBuffer();
                    }

                    var submissionCB = mtlCB.PrepareForSubmission();
                    arr.Set((uint)i, mtlCB);
                    submissionCB.commit();
                }

                MTLCommandBuffer finalCB = commandBuffers[commandBuffers.Length - 1] as MTLCommandBuffer;
                if (finalCB == null)
                {
                    var reusableCB = (MTLReusableCommandBuffer)commandBuffers[commandBuffers.Length - 1];
                    finalCB = reusableCB.RecordAndGetCommandBuffer();
                }
                lock (_submittedCommandsLock)
                {
                    var finalSubmissionCB = finalCB.PrepareForSubmission();
                    arr.Set((uint)commandBuffers.Length - 1, finalCB);
                    _submittedCBsMap.Add(finalSubmissionCB, arr);
                    if (fence != null)
                    {
                        MTLFence mtlFence = Util.AssertSubtype<Fence, MTLFence>(fence);
                        _submittedCBs.Add(finalSubmissionCB, mtlFence);
                    }

                    finalSubmissionCB.addCompletedHandler(_completionBlockLiteral);
                    finalSubmissionCB.commit();
                }
            }
        }

        private protected override void SubmitCommandsCore(CommandBuffer[] commandBuffers, Semaphore wait, Semaphore signal, Fence fence)
        {
            SubmitCommandsCore(commandBuffers, (Semaphore[])null, null, fence);
        }

        private protected override void PresentCore(Swapchain swapchain, Semaphore waitSemaphore, uint index)
        {
            MTLSwapchain mtlSC = Util.AssertSubtype<Swapchain, MTLSwapchain>(swapchain);
            MTLSwapchainFramebuffer mtlSCFB = Util.AssertSubtype<Framebuffer, MTLSwapchainFramebuffer>(
                mtlSC.Framebuffers[index]);
            IntPtr currentDrawablePtr = mtlSCFB.Drawable.NativePtr;
            if (currentDrawablePtr != IntPtr.Zero)
            {
                using (NSAutoreleasePool.Begin())
                {
                    MetalBindings.MTLCommandBuffer submitCB = _commandQueue.commandBuffer();
                    submitCB.presentDrawable(currentDrawablePtr);
                    submitCB.commit();
                }
            }
        }

        private protected override AcquireResult AcquireNextImageCore(
            Swapchain swapchain,
            Semaphore semaphore,
            Fence fence,
            out uint imageIndex)
        {
            MTLSwapchain mtlSC = Util.AssertSubtype<Swapchain, MTLSwapchain>(swapchain);
            imageIndex = mtlSC.AcquireNextImage();
            if (fence != null)
            {
                Util.AssertSubtype<Fence, MTLFence>(fence).Set();
            }

            return AcquireResult.Success;
        }
    }

    internal sealed class MonoPInvokeCallbackAttribute : Attribute
    {
        public MonoPInvokeCallbackAttribute(Type t) { }
    }
}
