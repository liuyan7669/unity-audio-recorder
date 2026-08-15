using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Cowart.AudioRecorder
{
    [ExecuteAlways]
    internal sealed class AudioClipCreationScheduler : MonoBehaviour
    {
        private static AudioClipCreationScheduler instance;

        private readonly List<AudioClipCreationRequest> requests =
            new List<AudioClipCreationRequest>();
        private int nextRequestIndex;

        internal static Task<AudioClip> Schedule(
            byte[] wavBytes,
            string clipName,
            int decodeFramesPerUpdate,
            CancellationToken cancellationToken)
        {
            AudioRecorderDriver.EnsureMainThread();
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<AudioClip>(cancellationToken);
            }

            AudioClipCreationScheduler scheduler = GetOrCreate();
            AudioClipCreationRequest request = new AudioClipCreationRequest(
                wavBytes,
                clipName,
                decodeFramesPerUpdate,
                cancellationToken);
            scheduler.requests.Add(request);
            return request.CompletionTask;
        }

        internal static void ProcessPendingRequestsForTests()
        {
            if (instance != null)
            {
                instance.ProcessPendingRequests();
            }
        }

        internal static void DestroyCurrentForTests()
        {
            if (instance == null)
            {
                return;
            }

            GameObject host = instance.gameObject;
            if (Application.isPlaying)
            {
                Destroy(host);
            }
            else
            {
                DestroyImmediate(host);
            }

            instance = null;
        }

        internal static void DestroyAudioClip(AudioClip clip)
        {
            if (clip == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(clip);
            }
            else
            {
                DestroyImmediate(clip);
            }
        }

        private static AudioClipCreationScheduler GetOrCreate()
        {
            if (instance != null)
            {
                return instance;
            }

            GameObject host = new GameObject("[Cowart.AudioClipCreation]");
            host.hideFlags = HideFlags.HideAndDontSave;
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(host);
            }

            instance = host.AddComponent<AudioClipCreationScheduler>();
            return instance;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            instance = null;
        }

        private void Update()
        {
            ProcessPendingRequests();
        }

        private void ProcessPendingRequests()
        {
            if (requests.Count == 0)
            {
                nextRequestIndex = 0;
                return;
            }

            if (nextRequestIndex >= requests.Count)
            {
                nextRequestIndex = 0;
            }

            if (requests[nextRequestIndex].ProcessOneStep())
            {
                requests.RemoveAt(nextRequestIndex);
                if (nextRequestIndex >= requests.Count)
                {
                    nextRequestIndex = 0;
                }
            }
            else
            {
                nextRequestIndex = (nextRequestIndex + 1) % requests.Count;
            }
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }

            for (int i = 0; i < requests.Count; i++)
            {
                requests[i].CancelFromSchedulerShutdown();
            }

            requests.Clear();
        }
    }

    internal sealed class AudioClipCreationRequest
    {
        private sealed class DecodedWav
        {
            public DecodedWav(Pcm16WavData wavData, float[] samples)
            {
                WavData = wavData;
                Samples = samples;
            }

            public Pcm16WavData WavData { get; }

            public float[] Samples { get; }
        }

        private enum CreationStage
        {
            WaitingForDecode,
            ParsingOnMainThread,
            DecodingOnMainThread,
            CreatingClip,
            WritingClip,
            Finished
        }

        private readonly byte[] wavBytes;
        private readonly string clipName;
        private readonly int decodeFramesPerUpdate;
        private readonly CancellationTokenSource lifetimeCancellation;
        private readonly CancellationToken cancellationToken;
        private readonly TaskCompletionSource<AudioClip> completionSource;

#if !UNITY_WEBGL || UNITY_EDITOR
        private readonly Task<DecodedWav> decodeTask;
#endif

        private CreationStage stage;
        private Pcm16WavData wavData;
        private float[] decodedSamples;
#if !UNITY_WEBGL || UNITY_EDITOR
        private float[] writeBuffer;
#endif
        private AudioClip clip;
        private int decodedFrames;
        private int writtenFrames;

        public AudioClipCreationRequest(
            byte[] wavBytes,
            string clipName,
            int decodeFramesPerUpdate,
            CancellationToken cancellationToken)
        {
            this.wavBytes = wavBytes;
            this.clipName = clipName;
            this.decodeFramesPerUpdate = decodeFramesPerUpdate;
            lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            this.cancellationToken = lifetimeCancellation.Token;
            completionSource = new TaskCompletionSource<AudioClip>(
                TaskCreationOptions.RunContinuationsAsynchronously);

#if UNITY_WEBGL && !UNITY_EDITOR
            stage = CreationStage.ParsingOnMainThread;
#else
            stage = CreationStage.WaitingForDecode;
            decodeTask = Task.Run(
                () =>
                {
                    this.cancellationToken.ThrowIfCancellationRequested();
                    Pcm16WavData parsed = Pcm16WavData.Parse(wavBytes);
                    float[] samples = parsed.DecodeAll(this.cancellationToken);
                    return new DecodedWav(parsed, samples);
                },
                this.cancellationToken);
#endif
        }

        public Task<AudioClip> CompletionTask => completionSource.Task;

        public bool ProcessOneStep()
        {
            if (stage == CreationStage.Finished)
            {
                return true;
            }

            if (cancellationToken.IsCancellationRequested)
            {
#if !UNITY_WEBGL || UNITY_EDITOR
                if (!decodeTask.IsCompleted)
                {
                    return false;
                }

                ObserveDecodeTaskFailure();
#endif
                Cancel();
                return true;
            }

            try
            {
                switch (stage)
                {
                    case CreationStage.WaitingForDecode:
                        return ProcessDecodedTask();

                    case CreationStage.ParsingOnMainThread:
                        wavData = Pcm16WavData.Parse(wavBytes);
                        decodedSamples = new float[wavData.SampleValueCount];
                        stage = CreationStage.DecodingOnMainThread;
                        return false;

                    case CreationStage.DecodingOnMainThread:
                        return DecodeNextMainThreadBatch();

                    case CreationStage.CreatingClip:
                        CreateClip();
                        stage = CreationStage.WritingClip;
                        return false;

                    case CreationStage.WritingClip:
                        return WriteNextClipBatch();

                    default:
                        return true;
                }
            }
            catch (OperationCanceledException)
            {
                Cancel();
                return true;
            }
            catch (Exception exception)
            {
                Fail(exception);
                return true;
            }
        }

        public void CancelFromSchedulerShutdown()
        {
            if (stage != CreationStage.Finished)
            {
                try
                {
                    lifetimeCancellation.Cancel();
                }
                catch (AggregateException)
                {
                    // Cancellation is already visible to the worker even if a caller callback threw.
                }

#if !UNITY_WEBGL || UNITY_EDITOR
                if (!decodeTask.IsCompleted)
                {
                    decodeTask.ContinueWith(
                        task => FinishCanceledAfterWorker(task),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    return;
                }

                ObserveDecodeTaskFailure();
#endif
                Cancel();
            }
        }

        private bool ProcessDecodedTask()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            stage = CreationStage.ParsingOnMainThread;
            return false;
#else
            if (!decodeTask.IsCompleted)
            {
                return false;
            }

            DecodedWav decoded = decodeTask.GetAwaiter().GetResult();
            wavData = decoded.WavData;
            decodedSamples = decoded.Samples;
            stage = CreationStage.CreatingClip;
            return false;
#endif
        }

        private void CreateClip()
        {
            cancellationToken.ThrowIfCancellationRequested();
            clip = AudioClip.Create(
                clipName,
                wavData.FrameCount,
                wavData.Channels,
                wavData.SampleRate,
                false);
            if (clip == null)
            {
                throw new InvalidOperationException("Unity failed to create the AudioClip.");
            }
        }

        private bool DecodeNextMainThreadBatch()
        {
            cancellationToken.ThrowIfCancellationRequested();
            int remainingFrames = wavData.FrameCount - decodedFrames;
            if (remainingFrames <= 0)
            {
                stage = CreationStage.CreatingClip;
                return false;
            }

            int frameCount = Math.Min(decodeFramesPerUpdate, remainingFrames);
            wavData.DecodeFrames(
                decodedFrames,
                frameCount,
                decodedSamples,
                checked(decodedFrames * wavData.Channels));
            decodedFrames += frameCount;
            return false;
        }

        private bool WriteNextClipBatch()
        {
            cancellationToken.ThrowIfCancellationRequested();
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!clip.SetData(decodedSamples, 0))
            {
                throw new InvalidOperationException("Unity failed to write PCM samples into the AudioClip.");
            }

            writtenFrames = wavData.FrameCount;
            Complete();
            return true;
#else
            int remainingFrames = wavData.FrameCount - writtenFrames;
            if (remainingFrames <= 0)
            {
                Complete();
                return true;
            }

            int frameCount = Math.Min(decodeFramesPerUpdate, remainingFrames);
            int sampleValueCount = checked(frameCount * wavData.Channels);
            if (writeBuffer == null || writeBuffer.Length != sampleValueCount)
            {
                writeBuffer = new float[sampleValueCount];
            }

            Array.Copy(
                decodedSamples,
                checked(writtenFrames * wavData.Channels),
                writeBuffer,
                0,
                sampleValueCount);

            cancellationToken.ThrowIfCancellationRequested();
            if (!clip.SetData(writeBuffer, writtenFrames))
            {
                throw new InvalidOperationException("Unity failed to write PCM samples into the AudioClip.");
            }

            writtenFrames += frameCount;
            return false;
#endif
        }

        private void Complete()
        {
            AudioClip completedClip = clip;
            clip = null;
            ReleaseManagedBuffers();
            stage = CreationStage.Finished;
            lifetimeCancellation.Dispose();
            completionSource.TrySetResult(completedClip);
        }

        private void Cancel()
        {
            DestroyTemporaryClip();
            ReleaseManagedBuffers();
            stage = CreationStage.Finished;
            lifetimeCancellation.Dispose();
            completionSource.TrySetCanceled();
        }

        private void Fail(Exception exception)
        {
            DestroyTemporaryClip();
            ReleaseManagedBuffers();
            stage = CreationStage.Finished;
            lifetimeCancellation.Dispose();
            completionSource.TrySetException(exception);
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        private void ObserveDecodeTaskFailure()
        {
            if (decodeTask.IsFaulted)
            {
                _ = decodeTask.Exception;
            }
        }

        private void FinishCanceledAfterWorker(Task<DecodedWav> task)
        {
            if (task.IsFaulted)
            {
                _ = task.Exception;
            }

            ReleaseManagedBuffers();
            stage = CreationStage.Finished;
            lifetimeCancellation.Dispose();
            completionSource.TrySetCanceled();
        }
#endif

        private void DestroyTemporaryClip()
        {
            AudioClipCreationScheduler.DestroyAudioClip(clip);
            clip = null;
        }

        private void ReleaseManagedBuffers()
        {
            wavData = null;
            decodedSamples = null;
#if !UNITY_WEBGL || UNITY_EDITOR
            writeBuffer = null;
#endif
        }
    }
}
