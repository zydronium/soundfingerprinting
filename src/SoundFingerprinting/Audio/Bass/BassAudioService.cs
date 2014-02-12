﻿namespace SoundFingerprinting.Audio.Bass
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;

    using SoundFingerprinting.Infrastructure;

    using Un4seen.Bass;
    using Un4seen.Bass.AddOn.Mix;
    using Un4seen.Bass.AddOn.Tags;
    using Un4seen.Bass.Misc;

    /// <summary>
    ///   Bass Proxy for Bass.Net API
    /// </summary>
    /// <remarks>
    ///   BASS is an audio library for use in Windows and Mac OSX software. 
    ///   Its purpose is to provide developers with powerful and efficient sample, stream (MP3, MP2, MP1, OGG, WAV, AIFF, custom generated, and more via add-ons), 
    ///   MOD music (XM, IT, S3M, MOD, MTM, UMX), MO3 music (MP3/OGG compressed MODs), and recording functions. 
    /// </remarks>
    public class BassAudioService : AudioService, IExtendedAudioService, ITagService
    {
        public const int DefaultSampleRate = 44100;

        public const int DefaultBufferLengthInSeconds = 20;

        private const string RegistrationEmail = "gleb.godonoga@gmail.com";

        private const string RegistrationKey = "2X155323152222";

        private const string FlacDllName = "bassflac.dll";

        private static readonly IReadOnlyCollection<string> BaasSupportedFormats = new[] { ".wav", "mp3", ".ogg", ".flac" };

        private static readonly object LockObject = new object();

        private static int initializedInstances;

        private readonly IBassServiceProxy bassServiceProxy;

        private bool alreadyDisposed;

        public BassAudioService() : this(DependencyResolver.Current.Get<IBassServiceProxy>())
        {
            // no op
        }

        private BassAudioService(IBassServiceProxy bassServiceProxy)
        {
            this.bassServiceProxy = bassServiceProxy;
            lock (LockObject)
            {
                if (!IsNativeBassLibraryInitialized)
                {
                    bassServiceProxy.RegisterBass(RegistrationEmail, RegistrationKey); // Call to avoid the freeware splash screen. Didn't see it, but maybe it will appear if the Forms are used
                    
                    string targetPath = GetTargetPathToLoadLibrariesFrom();

                    LoadBassLibraries(targetPath);

                    CheckIfFlacPluginIsLoaded(targetPath);

                    InitializeBassLibraryWithAudioDevices();

                    SetDefaultConfigs();

                    InitializeRecordingDevice();
                }

                initializedInstances++;
            }
        }

        ~BassAudioService()
        {
            Dispose(false);
        }

        public static bool IsNativeBassLibraryInitialized
        {
            get
            {
                return initializedInstances != 0;
            }
        }

        public bool IsRecordingSupported
        {
            get
            {
                return bassServiceProxy.GetRecordingDevice() != -1;
            }
        }

        public override IReadOnlyCollection<string> SupportedFormats
        {
            get
            {
                return BaasSupportedFormats;
            }
        }

        /// <summary>
        ///   Read mono from file
        /// </summary>
        /// <param name = "pathToFile">Name of the file</param>
        /// <param name = "sampleRate">Output sample rate</param>
        /// <param name = "secondsToRead">Milliseconds to read</param>
        /// <param name = "startAtSecond">Start millisecond</param>
        /// <returns>Array of samples</returns>
        public override float[] ReadMonoFromFile(string pathToFile, int sampleRate, int secondsToRead, int startAtSecond)
        {
            int stream = 0, mixerStream = 0;

            try
            {
                stream = CreateStream(pathToFile);
                mixerStream = CreateMixerStream(sampleRate, numberOfChannels: 1);
                CombineStreams(mixerStream, stream);
                SeekToSecondInCaseIfRequired(stream, startAtSecond);
                var chunks = ReadChannelDataFromUnderlyingMixerStream(mixerStream, secondsToRead, sampleRate);
                return ConcatenateChunksOfSamples(chunks);
            }
            finally
            {
                ReleaseStream(mixerStream, pathToFile);
                ReleaseStream(stream, pathToFile);
            }
        }

        public float[] ReadMonoFromUrl(string urlToResource, int sampleRate, int secondsToDownload)
        {
            int stream = Bass.BASS_StreamCreateURL(
                urlToResource, 0, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_MONO | BASSFlag.BASS_SAMPLE_FLOAT, null, IntPtr.Zero);

            if (stream == 0)
            {
                throw new Exception(Bass.BASS_ErrorGetCode().ToString());
            }

            int mixerStream = BassMix.BASS_Mixer_StreamCreate(sampleRate, 1, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_MONO | BASSFlag.BASS_SAMPLE_FLOAT);
            if (mixerStream == 0)
            {
                throw new Exception(Bass.BASS_ErrorGetCode().ToString());
            }

            if (!BassMix.BASS_Mixer_StreamAddChannel(mixerStream, stream, BASSFlag.BASS_MIXER_FILTER))
            {
                throw new Exception(Bass.BASS_ErrorGetCode().ToString());
            }

            float[] samples = ReadSamplesFromContinuousMixedStream(sampleRate, secondsToDownload, mixerStream);

            Bass.BASS_StreamFree(mixerStream);
            Bass.BASS_StreamFree(stream);

            return samples;
        }

        public float[] RecordFromMicrophone(int sampleRate, int secondsToRecord)
        {
            int stream = Bass.BASS_RecordStart(sampleRate, 1, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_MONO | BASSFlag.BASS_SAMPLE_FLOAT, null, IntPtr.Zero);

            if (stream == 0)
            {
                throw new Exception(Bass.BASS_ErrorGetCode().ToString());
            }

            int mixerStream = BassMix.BASS_Mixer_StreamCreate(sampleRate, 1, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_MONO | BASSFlag.BASS_SAMPLE_FLOAT);
            if (mixerStream == 0)
            {
                throw new Exception(Bass.BASS_ErrorGetCode().ToString());
            }

            if (!BassMix.BASS_Mixer_StreamAddChannel(mixerStream, stream, BASSFlag.BASS_MIXER_FILTER))
            {
                throw new Exception(Bass.BASS_ErrorGetCode().ToString());
            }

            float[] samples = ReadSamplesFromContinuousMixedStream(sampleRate, secondsToRecord, mixerStream);

            Bass.BASS_StreamFree(mixerStream);
            Bass.BASS_StreamFree(stream);

            return samples;
        }

        public float[] RecordFromMicrophoneToFile(string pathToFile, int sampleRate, int secondsToRecord)
        {
            int stream = Bass.BASS_RecordStart(sampleRate, 1, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_MONO | BASSFlag.BASS_SAMPLE_FLOAT, null, IntPtr.Zero);

            if (stream == 0)
            {
                throw new Exception(Bass.BASS_ErrorGetCode().ToString());
            }

            int mixerStream = BassMix.BASS_Mixer_StreamCreate(sampleRate, 1, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_MONO | BASSFlag.BASS_SAMPLE_FLOAT);
            if (mixerStream == 0)
            {
                throw new Exception(Bass.BASS_ErrorGetCode().ToString());
            }

            if (!BassMix.BASS_Mixer_StreamAddChannel(mixerStream, stream, BASSFlag.BASS_MIXER_FILTER))
            {
                throw new Exception(Bass.BASS_ErrorGetCode().ToString());
            }

            float[] samples = ReadSamplesFromContinuousMixedStream(sampleRate, secondsToRecord, mixerStream);
            using (WaveWriter waveWriter = new WaveWriter(pathToFile, mixerStream, true))
            {
                waveWriter.Write(samples, samples.Length);
            }

            Bass.BASS_StreamFree(mixerStream);
            Bass.BASS_StreamFree(stream);

            return samples;
        }

        public int PlayFile(string filename)
        {
            int stream = Bass.BASS_StreamCreateFile(filename, 0, 0, BASSFlag.BASS_DEFAULT);
            Bass.BASS_ChannelPlay(stream, false);
            return stream;
        }

        public void StopPlayingFile(int stream)
        {
            if (stream != 0)
            {
                Bass.BASS_StreamFree(stream);
            }
        }

        public void RecodeFileToMonoWave(string pathToFile, string outputPathToFile, int targetSampleRate)
        {
            int stream = Bass.BASS_StreamCreateFile(pathToFile, 0, 0, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_MONO | BASSFlag.BASS_SAMPLE_FLOAT);
            TAG_INFO tags = new TAG_INFO();
            BassTags.BASS_TAG_GetFromFile(stream, tags);
            int mixerStream = BassMix.BASS_Mixer_StreamCreate(targetSampleRate, 1, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_MONO | BASSFlag.BASS_SAMPLE_FLOAT);
            if (!BassMix.BASS_Mixer_StreamAddChannel(mixerStream, stream, BASSFlag.BASS_MIXER_FILTER))
            {
                throw new Exception(Bass.BASS_ErrorGetCode().ToString());
            }

            WaveWriter waveWriter = new WaveWriter(outputPathToFile, mixerStream, true);
            const int Length = 5512 * 10 * 4;
            float[] buffer = new float[Length];
            while (true)
            {
                int bytesRead = Bass.BASS_ChannelGetData(mixerStream, buffer, Length);
                if (bytesRead == 0)
                {
                    break;
                }

                waveWriter.Write(buffer, bytesRead);
            }

            waveWriter.Close();
        }

        public TagInfo GetTagInfo(string pathToAudioFile)
        {
            TAG_INFO tags = BassTags.BASS_TAG_GetFromFile(pathToAudioFile);
            if (tags == null)
            {
                return new TagInfo { IsEmpty = true };
            }

            int year;
            int.TryParse(tags.year, out year);
            TagInfo tag = new TagInfo
                {
                    Duration = tags.duration,
                    Album = tags.album,
                    Artist = tags.artist,
                    Title = tags.title,
                    AlbumArtist = tags.albumartist,
                    Genre = tags.genre,
                    Year = year,
                    Composer = tags.composer,
                    ISRC = tags.isrc
                };

            return tag;
        }

        protected override void Dispose(bool isDisposing)
        {
            if (!alreadyDisposed)
            {
                alreadyDisposed = true;

                if (isDisposing)
                {
                    // release managed resources
                }

                lock (LockObject)
                {
                    if (IsSafeToDisposeNativeBassLibrary())
                    {
                        // 0 - free all loaded plugins
                        if (!bassServiceProxy.PluginFree(0))
                        {
                            Trace.WriteLine("Could not unload plugins for Bass library.", "Error");
                        }

                        if (!bassServiceProxy.BassFree())
                        {
                            Trace.WriteLine("Could not free Bass library. Possible memory leak!", "Error");
                        }
                    }

                    initializedInstances--;
                }
            }
        }

        private float[] ReadSamplesFromContinuousMixedStream(int sampleRate, int secondsToDownload, int mixerStream)
        {
            float[] buffer = new float[secondsToDownload * sampleRate * 4];
            int totalBytesToRead = secondsToDownload * sampleRate * 4;
            int totalBytesRead = 0;
            List<float[]> chunks = new List<float[]>();
            while (totalBytesRead < totalBytesToRead)
            {
                int bytesRead = Bass.BASS_ChannelGetData(mixerStream, buffer, buffer.Length);

                if (bytesRead == -1)
                {
                    throw new Exception(Bass.BASS_ErrorGetCode().ToString());
                }

                if (bytesRead == 0)
                {
                    continue;
                }

                totalBytesRead += bytesRead;

                float[] chunk;
                if (totalBytesRead > totalBytesToRead)
                {
                    chunk = new float[(totalBytesToRead - (totalBytesRead - bytesRead)) / 4];
                    Array.Copy(buffer, chunk, (totalBytesToRead - (totalBytesRead - bytesRead)) / 4);
                }
                else
                {
                    chunk = new float[bytesRead / 4];
                    Array.Copy(buffer, chunk, bytesRead / 4);
                }

                chunks.Add(chunk);
            }

            return ConcatenateChunksOfSamples(chunks);
        }

        private bool IsSafeToDisposeNativeBassLibrary()
        {
            return initializedInstances == 1;
        }

        private void LoadBassLibraries(string targetPath)
        {
            // dummy calls to load bass libraries
            if (!bassServiceProxy.BassLoadMe(targetPath))
            {
                throw new BassAudioServiceException("Could not load bass native libraries from the following path: " + targetPath);
            }

            if (!bassServiceProxy.BassMixLoadMe(targetPath))
            {
                throw new BassAudioServiceException("Could not load bassmix library from the following path: " + targetPath);
            }

            if (!bassServiceProxy.BassFxLoadMe(targetPath))
            {
                throw new BassAudioServiceException("Could not load bassfx library from the following path: " + targetPath);
            }

            bassServiceProxy.GetVersion();
            bassServiceProxy.GetMixerVersion();
            bassServiceProxy.GetFxVersion();
        }

        private void InitializeBassLibraryWithAudioDevices()
        {
            if (!bassServiceProxy.Init(-1, DefaultSampleRate, BASSInit.BASS_DEVICE_DEFAULT | BASSInit.BASS_DEVICE_MONO))
            {
                Trace.WriteLine("Failed to find a sound device on running machine. Playing audio files will not be supported. " + bassServiceProxy.GetLastError(), "Warning");
                if (!Bass.BASS_Init(0, DefaultSampleRate, BASSInit.BASS_DEVICE_DEFAULT | BASSInit.BASS_DEVICE_MONO, IntPtr.Zero))
                {
                    throw new Exception(Bass.BASS_ErrorGetCode().ToString());
                }
            }
        }

        private void CheckIfFlacPluginIsLoaded(string targetPath)
        {
            var loadedPlugIns = bassServiceProxy.PluginLoadDirectory(targetPath);
            if (!loadedPlugIns.Any(p => p.Value.EndsWith(FlacDllName)))
            {
                Trace.WriteLine("Could not load bassflac.dll. FLAC format is not supported!", "Warning");
            }
        }

        private string GetTargetPathToLoadLibrariesFrom()
        {
            string executingPath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().GetName().CodeBase);
            if (string.IsNullOrEmpty(executingPath))
            {
                throw new BassAudioServiceException(
                    "Executing path of the application is null or empty. Could not find folders with native dll libraries.");
            }

            Uri uri = new Uri(executingPath);
            string targetPath = Path.Combine(uri.LocalPath, Utils.Is64Bit ? "x64" : "x86");
            return targetPath;
        }

        private void SetDefaultConfigs()
        {
            /*Set filter for anti aliasing*/
            if (!bassServiceProxy.SetConfig(BASSConfig.BASS_CONFIG_MIXER_FILTER, 50))
            {
                throw new BassAudioServiceException(bassServiceProxy.GetLastError());
            }

            /*Set floating parameters to be passed*/
            if (!bassServiceProxy.SetConfig(BASSConfig.BASS_CONFIG_FLOATDSP, true))
            {
                throw new Exception(bassServiceProxy.GetLastError());
            }
        }

        private void InitializeRecordingDevice()
        {
            const int DefaultDevice = -1;
            if (!bassServiceProxy.RecordInit(DefaultDevice))
            {
                Trace.WriteLine(
                    "No default recording device could be found on running machine. Recording is not supported: "
                    + bassServiceProxy.GetLastError(),
                    "Warning");
            }
        }

        private void NotifyErrorWhenReleasingMemoryStream(string pathToFile, int mixerStream)
        {
            Trace.WriteLine(
                "Could not release stream " + mixerStream + " generated from path " + pathToFile
                + ". Possible memory leak! Bass Error: " + bassServiceProxy.GetLastError(),
                "Error");
        }

        private void SeekToSecondInCaseIfRequired(int stream, int startAtSecond)
        {
            if (startAtSecond > 0)
            {
                if (!bassServiceProxy.ChannelSetPosition(stream, startAtSecond))
                {
                    throw new BassAudioServiceException(bassServiceProxy.GetLastError());
                }
            }
        }

        private void CombineStreams(int mixerStream, int stream)
        {
            if (!bassServiceProxy.CombineMixerStreams(mixerStream, stream, BASSFlag.BASS_MIXER_FILTER))
            {
                throw new BassAudioServiceException(bassServiceProxy.GetLastError());
            }
        }

        private int CreateMixerStream(int sampleRate, int numberOfChannels)
        {
            int mixerStream = bassServiceProxy.CreateMixerStream(
                sampleRate,
                numberOfChannels,
                BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_MONO | BASSFlag.BASS_SAMPLE_FLOAT);
            if (mixerStream == 0)
            {
                throw new BassAudioServiceException(bassServiceProxy.GetLastError());
            }

            return mixerStream;
        }

        private int CreateStream(string pathToFile)
        {
            // create streams for re-sampling
            int stream = bassServiceProxy.CreateStream(
                pathToFile, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_MONO | BASSFlag.BASS_SAMPLE_FLOAT);

            if (stream == 0)
            {
                throw new BassAudioServiceException(bassServiceProxy.GetLastError());
            }

            return stream;
        }

        private void ReleaseStream(int stream, string pathToFile)
        {
            if (stream != 0 && !bassServiceProxy.FreeStream(stream))
            {
                NotifyErrorWhenReleasingMemoryStream(pathToFile, stream);
            }
        }

        private List<float[]> ReadChannelDataFromUnderlyingMixerStream(int mixerStream, int secondsToRead, int sampleRate)
        {
            float[] buffer = new float[sampleRate * DefaultBufferLengthInSeconds]; // 20 seconds buffer
            List<float[]> chunks = new List<float[]>();
            int totalBytesToRead = secondsToRead == 0 ? int.MaxValue : secondsToRead * sampleRate * 4;
            int totalBytesRead = 0;
            while (totalBytesRead < totalBytesToRead)
            {
                // get re-sampled/mono data
                int bytesRead = bassServiceProxy.ChannelGetData(mixerStream, buffer, buffer.Length * 4);

                if (bytesRead == -1)
                {
                    throw new BassAudioServiceException(bassServiceProxy.GetLastError());
                }

                if (bytesRead == 0)
                {
                    break;
                }

                totalBytesRead += bytesRead;

                float[] chunk;

                if (totalBytesRead > totalBytesToRead)
                {
                    chunk = new float[(totalBytesToRead - (totalBytesRead - bytesRead)) / 4];
                    Array.Copy(buffer, chunk, (totalBytesToRead - (totalBytesRead - bytesRead)) / 4);
                }
                else
                {
                    chunk = new float[bytesRead / 4]; // each float contains 4 bytes
                    Array.Copy(buffer, chunk, bytesRead / 4);
                }

                chunks.Add(chunk);
            }

            if (totalBytesRead < (secondsToRead * sampleRate * 4))
            {
                throw new BassAudioServiceException(
                    "Could not read requested number of seconds " + secondsToRead + ", audio file is not that long");
            }

            return chunks;
        }
    }
}
