
using System;
using System.IO;
using System.Collections.Generic;
using OpenTK.Mathematics;
using OpenTK.Compute.OpenCL;

namespace Gepe3D
{
    public class HParticleSimulator
    {
        
        CLCommandQueue queue;
        UIntPtr[] workDimensions;
        
        CLEvent @event = new CLEvent();
        
        public readonly int ParticleCount;
        public readonly float[] PosData;
        
        
        public static float GRID_CELL_WIDTH = 0.6f; // used for the fluid effect radius
        
        public static float REST_DENSITY = 80f;
        
        public static int
            GridRowsX = 8,
            GridRowsY = 8,
            GridRowsZ = 8;
            
        public static float
            MAX_X = GRID_CELL_WIDTH * GridRowsX,
            MAX_Y = GRID_CELL_WIDTH * GridRowsY,
            MAX_Z = GRID_CELL_WIDTH * GridRowsZ;
        
        private int NUM_ITERATIONS = 2;
        
        private List<DistanceConstraint> distanceConstraints;
        public List<FluidConstraint> fluidConstraints;
        
        private readonly CLKernel
            kPredictPos,        // add external forces then predict positions with euler integration
            kGenNeighbours,     // add particles to bins corresponding to coordinates for easy proximity search
            kDistanceProject,   // project distance constraints (for soft bodies etc)
            kCalcLambdas,     // FLUID - calculate lambda at each particle (scalar for position adjustment)
            kAddLambdas,        // FLUID - adjust position estimates using lambda values
            kCorrectFluid,
            kUpdateVel,         // update final position and velocity with bounds collision
            kCalcVorticity,
            kApplyVortVisc,
            kCorrectVel;
        
        private readonly CLBuffer
            pos,        // positions
            vel,        // velocities
            epos,       // estimated positions
            imass,      // inverse masses
            lambdas,    // scalar for position adjustment
            corrections,
            vorticities,
            velCorrect;
        
        public HParticleSimulator(int particleCount)
        {
            this.ParticleCount = particleCount;
            PosData = new float[particleCount * 3];
            
            
            distanceConstraints = new List<DistanceConstraint>();
            fluidConstraints = new List<FluidConstraint>();
            
            
            ///////////////////
            // Set up OpenCL //
            ///////////////////
            
            // set up context & queue
            CLResultCode result;
            CLPlatform[] platforms;
            CLDevice[] devices;
            result = CL.GetPlatformIds(out platforms);
            result = CL.GetDeviceIds(platforms[0], DeviceType.Gpu, out devices);
            CLContext context = CL.CreateContext(new IntPtr(), 1, devices, new IntPtr(), new IntPtr(), out result);
            this.queue = CL.CreateCommandQueueWithProperties(context, devices[0], new IntPtr(), out result);
            this.workDimensions = new UIntPtr[] { new UIntPtr( (uint) particleCount) };
            
            // load kernels
            
            string commonFuncSource   = LoadSource("res/Kernels/common_funcs.cl"); // combine with other source strings to add common functions
            string pbdCommonSource    = LoadSource("res/Kernels/pbd_common.cl");
            string fluidProjectSource = LoadSource("res/Kernels/fluid_project.cl");
            CLProgram pbdProgram   = BuildClProgram(context, devices, commonFuncSource + pbdCommonSource);
            CLProgram fluidProgram = BuildClProgram(context, devices, commonFuncSource + fluidProjectSource);
            
            this.kPredictPos = CL.CreateKernel(pbdProgram, "predict_positions", out result);
            this.kUpdateVel = CL.CreateKernel(pbdProgram, "update_velocity", out result);
            this.kCalcLambdas = CL.CreateKernel(fluidProgram, "calculate_lambdas", out result);
            this.kAddLambdas = CL.CreateKernel(fluidProgram, "add_lambdas", out result);
            this.kCorrectFluid = CL.CreateKernel(fluidProgram, "correct_fluid_positions", out result);
            this.kCalcVorticity = CL.CreateKernel(fluidProgram, "calculate_vorticities", out result);
            this.kApplyVortVisc = CL.CreateKernel(fluidProgram, "apply_vorticity_viscosity", out result);
            this.kCorrectVel = CL.CreateKernel(fluidProgram, "correct_fluid_vel", out result);
            
            // create buffers
            UIntPtr bufferSize3 = new UIntPtr( (uint) particleCount * 3 * sizeof(float) );
            UIntPtr bufferSize1 = new UIntPtr( (uint) particleCount * 1 * sizeof(float) );
            this.pos       = CL.CreateBuffer(context, MemoryFlags.ReadWrite, bufferSize3, new IntPtr(), out result);
            this.vel       = CL.CreateBuffer(context, MemoryFlags.ReadWrite, bufferSize3, new IntPtr(), out result);
            this.epos      = CL.CreateBuffer(context, MemoryFlags.ReadWrite, bufferSize3, new IntPtr(), out result);
            this.imass     = CL.CreateBuffer(context, MemoryFlags.ReadWrite, bufferSize1, new IntPtr(), out result);
            this.lambdas   = CL.CreateBuffer(context, MemoryFlags.ReadWrite, bufferSize1, new IntPtr(), out result);
            this.corrections= CL.CreateBuffer(context, MemoryFlags.ReadWrite, bufferSize3, new IntPtr(), out result);
            this.vorticities= CL.CreateBuffer(context, MemoryFlags.ReadWrite, bufferSize3, new IntPtr(), out result);
            this.velCorrect= CL.CreateBuffer(context, MemoryFlags.ReadWrite, bufferSize3, new IntPtr(), out result);
            
            Random rand = new Random();
            float[] rands = new float[particleCount * 3];
            for (int i = 0; i < rands.Length; i++)
                rands[i] = (float) rand.NextDouble() * 2.5f;
            
            // fill buffers with zeroes
            float[] emptyFloat = new float[] {0};
            CL.EnqueueWriteBuffer<float>(queue, pos, false, new UIntPtr(), rands, null, out @event);
            CL.EnqueueFillBuffer<float>(queue, vel      , emptyFloat, new UIntPtr(), bufferSize3, null, out @event);
            CL.EnqueueFillBuffer<float>(queue, epos     , emptyFloat, new UIntPtr(), bufferSize3, null, out @event);
            CL.EnqueueFillBuffer<float>(queue, imass    , new float[] {1}, new UIntPtr(), bufferSize1, null, out @event);
            CL.EnqueueFillBuffer<float>(queue, lambdas  , emptyFloat, new UIntPtr(), bufferSize1, null, out @event);
            CL.EnqueueFillBuffer<float>(queue, corrections  , emptyFloat, new UIntPtr(), bufferSize3, null, out @event);
            CL.EnqueueFillBuffer<float>(queue, vorticities  , emptyFloat, new UIntPtr(), bufferSize3, null, out @event);
            CL.EnqueueFillBuffer<float>(queue, velCorrect  , emptyFloat, new UIntPtr(), bufferSize3, null, out @event);
            
            // ensure fills are completed
            CL.Flush(queue);
            CL.Finish(queue);
            
        }
        
        
        private string LoadSource(string filePath)
        {
            filePath = Path.Combine(Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory), filePath);
            return File.ReadAllText(filePath);
        }
        
        private CLProgram BuildClProgram(CLContext context, CLDevice[] devices, string source)
        {
            CLResultCode result;
            CLProgram program = CL.CreateProgramWithSource(context, source, out result);
            result = CL.BuildProgram(program, 1, devices, null, new IntPtr(), new IntPtr());
            
            if (result != CLResultCode.Success) {
                System.Console.WriteLine(result);
                byte[] logParam;
                CL.GetProgramBuildInfo(program, devices[0], ProgramBuildInfo.Log, out logParam);
                string error = System.Text.ASCIIEncoding.Default.GetString(logParam);
                System.Console.WriteLine(error);
            }
            return program;
        }
        
        
        
        public void Update(float delta)
        {
            
            CL.SetKernelArg<float>(kPredictPos, 0, delta);
            CL.SetKernelArg<CLBuffer>(kPredictPos, 1, pos);
            CL.SetKernelArg<CLBuffer>(kPredictPos, 2, vel);
            CL.SetKernelArg<CLBuffer>(kPredictPos, 3, epos);
            CL.EnqueueNDRangeKernel(queue, kPredictPos, 1, null, workDimensions, null, 0, null, out @event);
            
            
            
            CL.SetKernelArg<CLBuffer>(kCalcLambdas, 0, epos);
            CL.SetKernelArg<CLBuffer>(kCalcLambdas, 1, imass);
            CL.SetKernelArg<CLBuffer>(kCalcLambdas, 2, lambdas);
            CL.SetKernelArg<float>(kCalcLambdas, 3, GRID_CELL_WIDTH);
            CL.SetKernelArg<float>(kCalcLambdas, 4, REST_DENSITY);
            CL.EnqueueNDRangeKernel(queue, kCalcLambdas, 1, null, workDimensions, null, 0, null, out @event);
            
            
            CL.SetKernelArg<CLBuffer>(kAddLambdas, 0, epos);
            CL.SetKernelArg<CLBuffer>(kAddLambdas, 1, imass);
            CL.SetKernelArg<CLBuffer>(kAddLambdas, 2, lambdas);
            CL.SetKernelArg<float>(kAddLambdas, 3, GRID_CELL_WIDTH);
            CL.SetKernelArg<float>(kAddLambdas, 4, REST_DENSITY);
            CL.SetKernelArg<CLBuffer>(kAddLambdas, 5, corrections);
            CL.EnqueueNDRangeKernel(queue, kAddLambdas, 1, null, workDimensions, null, 0, null, out @event);
            
            CL.SetKernelArg<CLBuffer>(kCorrectFluid, 0, epos);
            CL.SetKernelArg<CLBuffer>(kCorrectFluid, 1, corrections);
            CL.EnqueueNDRangeKernel(queue, kCorrectFluid, 1, null, workDimensions, null, 0, null, out @event);
            
            
            CL.SetKernelArg<float>(kUpdateVel, 0, delta);
            CL.SetKernelArg<CLBuffer>(kUpdateVel, 1, pos);
            CL.SetKernelArg<CLBuffer>(kUpdateVel, 2, vel);
            CL.SetKernelArg<CLBuffer>(kUpdateVel, 3, epos);
            CL.SetKernelArg<float>(kUpdateVel, 4, MAX_X);
            CL.SetKernelArg<float>(kUpdateVel, 5, MAX_Y);
            CL.SetKernelArg<float>(kUpdateVel, 6, MAX_Z);
            CL.EnqueueNDRangeKernel(queue, kUpdateVel, 1, null, workDimensions, null, 0, null, out @event);
            
            
            CL.SetKernelArg<CLBuffer>(kCalcVorticity, 0, pos);
            CL.SetKernelArg<CLBuffer>(kCalcVorticity, 1, vel);
            CL.SetKernelArg<CLBuffer>(kCalcVorticity, 2, vorticities);
            CL.SetKernelArg<float>(kCalcVorticity, 3, GRID_CELL_WIDTH);
            CL.EnqueueNDRangeKernel(queue, kCalcVorticity, 1, null, workDimensions, null, 0, null, out @event);
            
            
            CL.SetKernelArg<CLBuffer>(kApplyVortVisc, 0, pos);
            CL.SetKernelArg<CLBuffer>(kApplyVortVisc, 1, vel);
            CL.SetKernelArg<CLBuffer>(kApplyVortVisc, 2, vorticities);
            CL.SetKernelArg<CLBuffer>(kApplyVortVisc, 3, velCorrect);
            CL.SetKernelArg<CLBuffer>(kApplyVortVisc, 4, imass);
            CL.SetKernelArg<float>(kApplyVortVisc, 5, GRID_CELL_WIDTH);
            CL.SetKernelArg<float>(kApplyVortVisc, 6, REST_DENSITY);
            CL.SetKernelArg<float>(kApplyVortVisc, 7, delta);
            CL.EnqueueNDRangeKernel(queue, kApplyVortVisc, 1, null, workDimensions, null, 0, null, out @event);
            
            
            CL.SetKernelArg<CLBuffer>(kCorrectVel, 0, vel);
            CL.SetKernelArg<CLBuffer>(kCorrectVel, 1, velCorrect);
            CL.EnqueueNDRangeKernel(queue, kCorrectVel, 1, null, workDimensions, null, 0, null, out @event);
            
            
            CL.EnqueueReadBuffer<float>(queue, pos, false, new UIntPtr(), PosData, null, out @event);
            
            CL.Flush(queue);
            CL.Finish(queue);
        }
        
        
    }
}