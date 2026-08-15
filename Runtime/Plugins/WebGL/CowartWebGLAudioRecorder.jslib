var CowartWebGLAudioRecorderLibrary = {
  $CowartWebGLAudioRecorder: {
    stateCallback: 0,
    dataCallback: 0,
    levelCallback: 0,
    streamCallback: 0,
    pending: false,
    recording: false,
    finalizing: false,
    stopRequested: false,
    sessionId: 0,
    activeSessionId: 0,
    stream: null,
    audioContext: null,
    sourceNode: null,
    processorNode: null,
    pcmPageSize: 1024 * 1024,
    pcmPages: [],
    pcmPageOffset: 0,
    pcmByteLength: 0,
    inputSampleRate: 0,
    targetSampleRate: 16000,
    maximumOutputFrames: 0,
    capturedOutputFrameCount: 0,
    resamplePhase: 0,
    resampleAccumulator: 0,
    resampleAccumulatorWeight: 0,
    resampleInputFrameCount: 0,
    resampleOutputFrameCount: 0,
    streamChunkMilliseconds: 0,
    streamPcmBuffer: new Uint8Array(0),
    streamSequence: 0,
    streamOutputFrameCount: 0,
    maxTimer: 0,
    lastObjectUrl: '',
    playbackElement: null,
    textEncoder: null,

    allocateUtf8: function (value) {
      if (!CowartWebGLAudioRecorder.textEncoder) {
        CowartWebGLAudioRecorder.textEncoder = new TextEncoder();
      }

      var bytes = CowartWebGLAudioRecorder.textEncoder.encode(value || '');
      var pointer = _malloc(Math.max(bytes.length, 1));
      if (!pointer) {
        throw new Error('Failed to allocate UTF-8 callback memory.');
      }

      if (bytes.length > 0) {
        HEAPU8.set(bytes, pointer);
      }

      return { pointer: pointer, length: bytes.length };
    },

    notifyState: function (state, message) {
      if (!CowartWebGLAudioRecorder.stateCallback) {
        return;
      }

      var encoded = CowartWebGLAudioRecorder.allocateUtf8(message || '');
      try {
        {{{ makeDynCall('viii', 'CowartWebGLAudioRecorder.stateCallback') }}}(
          state,
          encoded.pointer,
          encoded.length
        );
      } finally {
        _free(encoded.pointer);
      }
    },

    notifyLevel: function (rms, peak) {
      if (!CowartWebGLAudioRecorder.levelCallback) {
        return;
      }

      {{{ makeDynCall('vff', 'CowartWebGLAudioRecorder.levelCallback') }}}(
        rms,
        peak
      );
    },

    notifyStream: function (pcmBytes, isLast, sessionId) {
      if (!CowartWebGLAudioRecorder.streamCallback ||
          sessionId !== CowartWebGLAudioRecorder.activeSessionId) {
        return;
      }

      var byteLength = pcmBytes ? pcmBytes.length : 0;
      var pointer = _malloc(Math.max(byteLength, 1));
      if (!pointer) {
        throw new Error('Failed to allocate streaming PCM callback memory.');
      }

      if (byteLength > 0) {
        HEAPU8.set(pcmBytes, pointer);
      }

      var sequence = CowartWebGLAudioRecorder.streamSequence;
      var timestampMilliseconds = Math.round(
        CowartWebGLAudioRecorder.streamOutputFrameCount * 1000 /
        CowartWebGLAudioRecorder.targetSampleRate
      );
      CowartWebGLAudioRecorder.streamSequence++;
      CowartWebGLAudioRecorder.streamOutputFrameCount += byteLength / 2;
      try {
        {{{ makeDynCall('viiiiii', 'CowartWebGLAudioRecorder.streamCallback') }}}(
          pointer,
          byteLength,
          sequence,
          timestampMilliseconds,
          sequence === 0 ? 1 : 0,
          isLast ? 1 : 0
        );
      } finally {
        _free(pointer);
      }
    },

    stopStreamTracks: function (stream) {
      if (!stream) {
        return;
      }

      try {
        var tracks = stream.getTracks();
        for (var i = 0; i < tracks.length; i++) {
          tracks[i].stop();
        }
      } catch (ignoredStreamError) {
      }
    },

    closeCapture: function () {
      if (CowartWebGLAudioRecorder.maxTimer) {
        try {
          clearTimeout(CowartWebGLAudioRecorder.maxTimer);
        } catch (ignoredTimerError) {
        }
        CowartWebGLAudioRecorder.maxTimer = 0;
      }

      if (CowartWebGLAudioRecorder.processorNode) {
        var processorNode = CowartWebGLAudioRecorder.processorNode;
        CowartWebGLAudioRecorder.processorNode = null;
        try {
          processorNode.onaudioprocess = null;
          processorNode.disconnect();
        } catch (ignoredProcessorError) {
        }
      }

      if (CowartWebGLAudioRecorder.sourceNode) {
        var sourceNode = CowartWebGLAudioRecorder.sourceNode;
        CowartWebGLAudioRecorder.sourceNode = null;
        try {
          sourceNode.disconnect();
        } catch (ignoredSourceError) {
        }
      }

      if (CowartWebGLAudioRecorder.stream) {
        var captureStream = CowartWebGLAudioRecorder.stream;
        CowartWebGLAudioRecorder.stream = null;
        CowartWebGLAudioRecorder.stopStreamTracks(captureStream);
      }

      if (CowartWebGLAudioRecorder.audioContext) {
        var audioContext = CowartWebGLAudioRecorder.audioContext;
        CowartWebGLAudioRecorder.audioContext = null;
        try {
          var closePromise = audioContext.close();
          if (closePromise && closePromise.catch) {
            closePromise.catch(function () {});
          }
        } catch (ignoredContextError) {
        }
      }
    },

    abortCapture: function () {
      CowartWebGLAudioRecorder.sessionId++;
      CowartWebGLAudioRecorder.activeSessionId = 0;
      CowartWebGLAudioRecorder.pending = false;
      CowartWebGLAudioRecorder.recording = false;
      CowartWebGLAudioRecorder.finalizing = false;
      CowartWebGLAudioRecorder.stopRequested = false;
      CowartWebGLAudioRecorder.resetPcmPages();
      CowartWebGLAudioRecorder.streamPcmBuffer = new Uint8Array(0);
      CowartWebGLAudioRecorder.resetResamplerState();
      CowartWebGLAudioRecorder.maximumOutputFrames = 0;
      CowartWebGLAudioRecorder.capturedOutputFrameCount = 0;
      CowartWebGLAudioRecorder.closeCapture();
    },

    disposePlaybackResources: function (playbackElement, objectUrl) {
      if (playbackElement) {
        try {
          playbackElement.pause();
          playbackElement.src = '';
        } catch (ignoredPlaybackError) {
        }
      }

      if (objectUrl) {
        try {
          URL.revokeObjectURL(objectUrl);
        } catch (ignoredObjectUrlError) {
        }
      }
    },

    clearLastRecording: function () {
      var playbackElement = CowartWebGLAudioRecorder.playbackElement;
      var objectUrl = CowartWebGLAudioRecorder.lastObjectUrl;
      CowartWebGLAudioRecorder.playbackElement = null;
      CowartWebGLAudioRecorder.lastObjectUrl = '';
      CowartWebGLAudioRecorder.disposePlaybackResources(playbackElement, objectUrl);
    },

    resetPcmPages: function () {
      var pages = CowartWebGLAudioRecorder.pcmPages;
      CowartWebGLAudioRecorder.pcmPages = [];
      CowartWebGLAudioRecorder.pcmPageOffset = 0;
      CowartWebGLAudioRecorder.pcmByteLength = 0;
      for (var i = 0; i < pages.length; i++) {
        pages[i] = null;
      }
      pages.length = 0;
    },

    appendPcmBytes: function (pcmBytes) {
      var sourceOffset = 0;
      while (sourceOffset < pcmBytes.length) {
        var pages = CowartWebGLAudioRecorder.pcmPages;
        var page = pages.length > 0 ? pages[pages.length - 1] : null;
        if (!page || CowartWebGLAudioRecorder.pcmPageOffset >= page.length) {
          page = new Uint8Array(CowartWebGLAudioRecorder.pcmPageSize);
          pages.push(page);
          CowartWebGLAudioRecorder.pcmPageOffset = 0;
        }

        var copyLength = Math.min(
          page.length - CowartWebGLAudioRecorder.pcmPageOffset,
          pcmBytes.length - sourceOffset
        );
        page.set(
          pcmBytes.subarray(sourceOffset, sourceOffset + copyLength),
          CowartWebGLAudioRecorder.pcmPageOffset
        );
        sourceOffset += copyLength;
        CowartWebGLAudioRecorder.pcmPageOffset += copyLength;
        CowartWebGLAudioRecorder.pcmByteLength += copyLength;
      }
    },

    detachPcmPages: function () {
      var snapshot = {
        pages: CowartWebGLAudioRecorder.pcmPages,
        byteLength: CowartWebGLAudioRecorder.pcmByteLength,
        released: false
      };
      CowartWebGLAudioRecorder.pcmPages = [];
      CowartWebGLAudioRecorder.pcmPageOffset = 0;
      CowartWebGLAudioRecorder.pcmByteLength = 0;
      return snapshot;
    },

    releasePcmPages: function (snapshot) {
      if (!snapshot || snapshot.released) {
        return;
      }

      snapshot.released = true;
      for (var i = 0; i < snapshot.pages.length; i++) {
        snapshot.pages[i] = null;
      }
      snapshot.pages.length = 0;
      snapshot.byteLength = 0;
    },

    resetResamplerState: function () {
      CowartWebGLAudioRecorder.resamplePhase = 0;
      CowartWebGLAudioRecorder.resampleAccumulator = 0;
      CowartWebGLAudioRecorder.resampleAccumulatorWeight = 0;
      CowartWebGLAudioRecorder.resampleInputFrameCount = 0;
      CowartWebGLAudioRecorder.resampleOutputFrameCount = 0;
    },

    resampleContinuous: function (input) {
      var inputRate = CowartWebGLAudioRecorder.inputSampleRate;
      var outputRate = CowartWebGLAudioRecorder.targetSampleRate;
      if (!isFinite(inputRate) || inputRate <= 0 ||
          !isFinite(outputRate) || outputRate <= 0) {
        throw new Error('The browser reported an invalid audio sample rate.');
      }

      var outputLength = Math.floor(
        (CowartWebGLAudioRecorder.resamplePhase + input.length * outputRate) /
        inputRate
      );
      var output = new Float32Array(outputLength);
      var outputIndex = 0;
      var phase = CowartWebGLAudioRecorder.resamplePhase;
      var accumulator = CowartWebGLAudioRecorder.resampleAccumulator;
      var accumulatorWeight = CowartWebGLAudioRecorder.resampleAccumulatorWeight;

      for (var inputIndex = 0; inputIndex < input.length; inputIndex++) {
        var remainingWeight = outputRate;
        while (remainingWeight > 0) {
          var weight = Math.min(remainingWeight, inputRate - phase);
          accumulator += input[inputIndex] * weight;
          accumulatorWeight += weight;
          phase += weight;
          remainingWeight -= weight;

          if (phase >= inputRate) {
            output[outputIndex++] = accumulatorWeight > 0
              ? accumulator / accumulatorWeight
              : 0;
            phase = 0;
            accumulator = 0;
            accumulatorWeight = 0;
          }
        }
      }

      if (outputIndex !== outputLength) {
        throw new Error('The continuous audio resampler produced an invalid frame count.');
      }

      CowartWebGLAudioRecorder.resamplePhase = phase;
      CowartWebGLAudioRecorder.resampleAccumulator = accumulator;
      CowartWebGLAudioRecorder.resampleAccumulatorWeight = accumulatorWeight;
      CowartWebGLAudioRecorder.resampleInputFrameCount += input.length;
      CowartWebGLAudioRecorder.resampleOutputFrameCount += output.length;
      return output;
    },

    flushResampler: function () {
      var shouldEmit = CowartWebGLAudioRecorder.resampleAccumulatorWeight > 0 &&
        CowartWebGLAudioRecorder.resamplePhase * 2 >=
          CowartWebGLAudioRecorder.inputSampleRate;
      var output = new Float32Array(shouldEmit ? 1 : 0);
      if (shouldEmit) {
        output[0] = CowartWebGLAudioRecorder.resampleAccumulator /
          CowartWebGLAudioRecorder.resampleAccumulatorWeight;
        CowartWebGLAudioRecorder.resampleOutputFrameCount++;
      }

      CowartWebGLAudioRecorder.resamplePhase = 0;
      CowartWebGLAudioRecorder.resampleAccumulator = 0;
      CowartWebGLAudioRecorder.resampleAccumulatorWeight = 0;
      return output;
    },

    encodePcm16: function (samples) {
      var buffer = new ArrayBuffer(samples.length * 2);
      var view = new DataView(buffer);
      for (var sampleIndex = 0; sampleIndex < samples.length; sampleIndex++) {
        var sample = Math.max(-1, Math.min(1, samples[sampleIndex]));
        view.setInt16(
          sampleIndex * 2,
          sample < 0 ? sample * 32768 : sample * 32767,
          true
        );
      }

      return new Uint8Array(buffer);
    },

    appendStreamPcmBytes: function (pcmBytes, sessionId) {
      if (CowartWebGLAudioRecorder.streamChunkMilliseconds <= 0 ||
          !CowartWebGLAudioRecorder.streamCallback ||
          sessionId !== CowartWebGLAudioRecorder.activeSessionId ||
          (!CowartWebGLAudioRecorder.recording &&
           !CowartWebGLAudioRecorder.finalizing)) {
        return;
      }

      var previous = CowartWebGLAudioRecorder.streamPcmBuffer;
      var combined = new Uint8Array(previous.length + pcmBytes.length);
      combined.set(previous, 0);
      combined.set(pcmBytes, previous.length);
      CowartWebGLAudioRecorder.streamPcmBuffer = combined;

      var chunkByteLength = Math.max(
        2,
        Math.round(
          CowartWebGLAudioRecorder.targetSampleRate *
          CowartWebGLAudioRecorder.streamChunkMilliseconds /
          1000
        ) * 2
      );
      while (CowartWebGLAudioRecorder.streamPcmBuffer.length >= chunkByteLength) {
        if (sessionId !== CowartWebGLAudioRecorder.activeSessionId ||
            (!CowartWebGLAudioRecorder.recording &&
             !CowartWebGLAudioRecorder.finalizing)) {
          return;
        }

        var buffered = CowartWebGLAudioRecorder.streamPcmBuffer;
        var streamChunk = new Uint8Array(
          buffered.subarray(0, chunkByteLength)
        );
        CowartWebGLAudioRecorder.streamPcmBuffer = new Uint8Array(
          buffered.subarray(chunkByteLength)
        );
        CowartWebGLAudioRecorder.notifyStream(streamChunk, false, sessionId);
      }
    },

    flushStream: function (sessionId) {
      if (CowartWebGLAudioRecorder.streamChunkMilliseconds <= 0 ||
          !CowartWebGLAudioRecorder.streamCallback ||
          sessionId !== CowartWebGLAudioRecorder.activeSessionId) {
        return;
      }

      var remaining = CowartWebGLAudioRecorder.streamPcmBuffer;
      CowartWebGLAudioRecorder.streamPcmBuffer = new Uint8Array(0);
      CowartWebGLAudioRecorder.notifyStream(remaining, true, sessionId);
    },

    appendTargetSamples: function (samples, sessionId) {
      if (sessionId !== CowartWebGLAudioRecorder.activeSessionId ||
          (!CowartWebGLAudioRecorder.recording &&
           !CowartWebGLAudioRecorder.finalizing)) {
        return;
      }

      var remainingFrames = Math.max(
        0,
        CowartWebGLAudioRecorder.maximumOutputFrames -
        CowartWebGLAudioRecorder.capturedOutputFrameCount
      );
      var acceptedFrameCount = Math.min(samples.length, remainingFrames);
      if (acceptedFrameCount > 0) {
        var acceptedSamples = acceptedFrameCount === samples.length
          ? samples
          : samples.subarray(0, acceptedFrameCount);
        var pcmBytes = CowartWebGLAudioRecorder.encodePcm16(acceptedSamples);
        CowartWebGLAudioRecorder.appendPcmBytes(pcmBytes);
        CowartWebGLAudioRecorder.capturedOutputFrameCount += acceptedFrameCount;
        CowartWebGLAudioRecorder.appendStreamPcmBytes(pcmBytes, sessionId);
      }

      if (CowartWebGLAudioRecorder.capturedOutputFrameCount >=
            CowartWebGLAudioRecorder.maximumOutputFrames &&
          sessionId === CowartWebGLAudioRecorder.activeSessionId &&
          CowartWebGLAudioRecorder.recording &&
          !CowartWebGLAudioRecorder.finalizing) {
        CowartWebGLAudioRecorder.finishRecording(sessionId);
      }
    },

    handleAudioProcess: function (event, sessionId) {
      if (sessionId !== CowartWebGLAudioRecorder.activeSessionId ||
          !CowartWebGLAudioRecorder.recording ||
          CowartWebGLAudioRecorder.finalizing) {
        return;
      }

      try {
        var inputBuffer = event.inputBuffer;
        var frameCount = inputBuffer.length;
        var channelCount = inputBuffer.numberOfChannels;
        var mono = new Float32Array(frameCount);
        for (var channel = 0; channel < channelCount; channel++) {
          var channelSamples = inputBuffer.getChannelData(channel);
          for (var frame = 0; frame < frameCount; frame++) {
            mono[frame] += channelSamples[frame] / channelCount;
          }
        }

        var squareSum = 0;
        var peak = 0;
        for (var levelFrame = 0; levelFrame < frameCount; levelFrame++) {
          var absolute = Math.abs(mono[levelFrame]);
          squareSum += absolute * absolute;
          peak = Math.max(peak, absolute);
        }
        CowartWebGLAudioRecorder.notifyLevel(
          frameCount > 0 ? Math.sqrt(squareSum / frameCount) : 0,
          peak
        );
        CowartWebGLAudioRecorder.appendTargetSamples(
          CowartWebGLAudioRecorder.resampleContinuous(mono),
          sessionId
        );
      } catch (error) {
        CowartWebGLAudioRecorder.failCapture(sessionId, error);
      }
    },

    createWavHeader: function (pcmByteLength, sampleRate) {
      var buffer = new ArrayBuffer(44);
      var view = new DataView(buffer);

      function writeAscii(offset, value) {
        for (var i = 0; i < value.length; i++) {
          view.setUint8(offset + i, value.charCodeAt(i));
        }
      }

      writeAscii(0, 'RIFF');
      view.setUint32(4, 36 + pcmByteLength, true);
      writeAscii(8, 'WAVE');
      writeAscii(12, 'fmt ');
      view.setUint32(16, 16, true);
      view.setUint16(20, 1, true);
      view.setUint16(22, 1, true);
      view.setUint32(24, sampleRate, true);
      view.setUint32(28, sampleRate * 2, true);
      view.setUint16(32, 2, true);
      view.setUint16(34, 16, true);
      writeAscii(36, 'data');
      view.setUint32(40, pcmByteLength, true);

      return new Uint8Array(buffer);
    },

    createWavBlob: function (wavHeader, snapshot) {
      var blobParts = [wavHeader];
      var remaining = snapshot.byteLength;
      for (var i = 0; i < snapshot.pages.length && remaining > 0; i++) {
        var page = snapshot.pages[i];
        var pageLength = Math.min(page.length, remaining);
        blobParts.push(pageLength === page.length ? page : page.subarray(0, pageLength));
        remaining -= pageLength;
      }

      if (remaining !== 0) {
        throw new Error('The captured PCM page data is incomplete.');
      }

      return new Blob(blobParts, { type: 'audio/wav' });
    },

    sendRecording: function (wavHeader, snapshot, sampleRate, durationMilliseconds) {
      if (!CowartWebGLAudioRecorder.dataCallback) {
        throw new Error('Unity audio data callback is not registered.');
      }

      var wavByteLength = wavHeader.length + snapshot.byteLength;
      var pointer = _malloc(wavByteLength);
      if (!pointer) {
        throw new Error('Failed to allocate WAV callback memory.');
      }

      try {
        HEAPU8.set(wavHeader, pointer);
        var destinationOffset = wavHeader.length;
        var remaining = snapshot.byteLength;
        for (var i = 0; i < snapshot.pages.length && remaining > 0; i++) {
          var page = snapshot.pages[i];
          var pageLength = Math.min(page.length, remaining);
          HEAPU8.set(
            pageLength === page.length ? page : page.subarray(0, pageLength),
            pointer + destinationOffset
          );
          destinationOffset += pageLength;
          remaining -= pageLength;
        }

        if (remaining !== 0 || destinationOffset !== wavByteLength) {
          throw new Error('The captured PCM page data is incomplete.');
        }

        {{{ makeDynCall('viiiii', 'CowartWebGLAudioRecorder.dataCallback') }}}(
          pointer,
          wavByteLength,
          sampleRate,
          1,
          durationMilliseconds
        );
      } finally {
        _free(pointer);
      }
    },

    finishRecording: function (sessionId) {
      if (sessionId !== CowartWebGLAudioRecorder.activeSessionId ||
          !CowartWebGLAudioRecorder.recording ||
          CowartWebGLAudioRecorder.finalizing) {
        return false;
      }

      CowartWebGLAudioRecorder.recording = false;
      CowartWebGLAudioRecorder.pending = false;
      CowartWebGLAudioRecorder.finalizing = true;
      CowartWebGLAudioRecorder.stopRequested = false;
      var targetRate = CowartWebGLAudioRecorder.targetSampleRate;
      var pcmSnapshot = null;
      var wavHeader = null;
      var durationMilliseconds = 0;
      var previousPlaybackElement = CowartWebGLAudioRecorder.playbackElement;
      var previousObjectUrl = CowartWebGLAudioRecorder.lastObjectUrl;
      var candidatePlaybackElement = null;
      var candidateObjectUrl = '';

      try {
        CowartWebGLAudioRecorder.notifyState(3, '');
        if (sessionId !== CowartWebGLAudioRecorder.activeSessionId ||
            !CowartWebGLAudioRecorder.finalizing) {
          return false;
        }

        CowartWebGLAudioRecorder.appendTargetSamples(
          CowartWebGLAudioRecorder.flushResampler(),
          sessionId
        );
        if (sessionId !== CowartWebGLAudioRecorder.activeSessionId ||
            !CowartWebGLAudioRecorder.finalizing) {
          return false;
        }

        CowartWebGLAudioRecorder.flushStream(sessionId);
        if (sessionId !== CowartWebGLAudioRecorder.activeSessionId ||
            !CowartWebGLAudioRecorder.finalizing) {
          return false;
        }

        pcmSnapshot = CowartWebGLAudioRecorder.detachPcmPages();
        if (pcmSnapshot.byteLength === 0) {
          throw new Error('The browser did not capture any microphone samples.');
        }

        wavHeader = CowartWebGLAudioRecorder.createWavHeader(
          pcmSnapshot.byteLength,
          targetRate
        );
        durationMilliseconds = Math.round(pcmSnapshot.byteLength * 500 / targetRate);
        var blob = CowartWebGLAudioRecorder.createWavBlob(wavHeader, pcmSnapshot);
        candidateObjectUrl = URL.createObjectURL(blob);
        candidatePlaybackElement = new Audio(candidateObjectUrl);
        candidatePlaybackElement.preload = 'metadata';
        candidatePlaybackElement.load();
      } catch (error) {
        CowartWebGLAudioRecorder.disposePlaybackResources(
          candidatePlaybackElement,
          candidateObjectUrl
        );
        CowartWebGLAudioRecorder.releasePcmPages(pcmSnapshot);
        if (sessionId === CowartWebGLAudioRecorder.activeSessionId &&
            CowartWebGLAudioRecorder.finalizing) {
          CowartWebGLAudioRecorder.resetPcmPages();
          CowartWebGLAudioRecorder.streamPcmBuffer = new Uint8Array(0);
          CowartWebGLAudioRecorder.resetResamplerState();
          CowartWebGLAudioRecorder.maximumOutputFrames = 0;
          CowartWebGLAudioRecorder.capturedOutputFrameCount = 0;
          CowartWebGLAudioRecorder.closeCapture();
          CowartWebGLAudioRecorder.activeSessionId = 0;
          CowartWebGLAudioRecorder.finalizing = false;
          CowartWebGLAudioRecorder.stopRequested = false;
          CowartWebGLAudioRecorder.sessionId++;
          try {
            CowartWebGLAudioRecorder.notifyState(
              1,
              'Failed to create the browser WAV recording: ' + String(error)
            );
          } catch (ignoredFailureCallbackError) {
          }
        }
        return false;
      }

      CowartWebGLAudioRecorder.streamPcmBuffer = new Uint8Array(0);
      CowartWebGLAudioRecorder.resetResamplerState();
      CowartWebGLAudioRecorder.maximumOutputFrames = 0;
      CowartWebGLAudioRecorder.capturedOutputFrameCount = 0;
      CowartWebGLAudioRecorder.closeCapture();
      CowartWebGLAudioRecorder.activeSessionId = 0;
      CowartWebGLAudioRecorder.finalizing = false;
      CowartWebGLAudioRecorder.stopRequested = false;
      CowartWebGLAudioRecorder.sessionId++;
      CowartWebGLAudioRecorder.playbackElement = candidatePlaybackElement;
      CowartWebGLAudioRecorder.lastObjectUrl = candidateObjectUrl;
      try {
        CowartWebGLAudioRecorder.sendRecording(
          wavHeader,
          pcmSnapshot,
          targetRate,
          durationMilliseconds
        );
        CowartWebGLAudioRecorder.disposePlaybackResources(
          previousPlaybackElement,
          previousObjectUrl
        );
        return true;
      } catch (callbackError) {
        if (CowartWebGLAudioRecorder.playbackElement === candidatePlaybackElement &&
            CowartWebGLAudioRecorder.lastObjectUrl === candidateObjectUrl) {
          CowartWebGLAudioRecorder.playbackElement = previousPlaybackElement;
          CowartWebGLAudioRecorder.lastObjectUrl = previousObjectUrl;
          CowartWebGLAudioRecorder.disposePlaybackResources(
            candidatePlaybackElement,
            candidateObjectUrl
          );
        } else if (CowartWebGLAudioRecorder.playbackElement !== previousPlaybackElement ||
                   CowartWebGLAudioRecorder.lastObjectUrl !== previousObjectUrl) {
          CowartWebGLAudioRecorder.disposePlaybackResources(
            previousPlaybackElement,
            previousObjectUrl
          );
        }

        if (CowartWebGLAudioRecorder.activeSessionId === 0) {
          try {
            CowartWebGLAudioRecorder.notifyState(
              1,
              'Failed to return the browser WAV recording: ' + String(callbackError)
            );
          } catch (ignoredFailureCallbackError) {
          }
        }
        return false;
      } finally {
        CowartWebGLAudioRecorder.releasePcmPages(pcmSnapshot);
      }
    },

    failCapture: function (sessionId, error) {
      if (sessionId !== CowartWebGLAudioRecorder.activeSessionId ||
          CowartWebGLAudioRecorder.finalizing ||
          (!CowartWebGLAudioRecorder.pending &&
           !CowartWebGLAudioRecorder.recording)) {
        return;
      }

      CowartWebGLAudioRecorder.pending = false;
      CowartWebGLAudioRecorder.recording = false;
      CowartWebGLAudioRecorder.finalizing = false;
      CowartWebGLAudioRecorder.stopRequested = false;
      CowartWebGLAudioRecorder.resetPcmPages();
      CowartWebGLAudioRecorder.streamPcmBuffer = new Uint8Array(0);
      CowartWebGLAudioRecorder.resetResamplerState();
      CowartWebGLAudioRecorder.maximumOutputFrames = 0;
      CowartWebGLAudioRecorder.capturedOutputFrameCount = 0;
      CowartWebGLAudioRecorder.closeCapture();
      CowartWebGLAudioRecorder.activeSessionId = 0;
      CowartWebGLAudioRecorder.sessionId++;
      try {
        CowartWebGLAudioRecorder.notifyState(
          1,
          'Browser audio capture failed: ' + String(error)
        );
      } catch (ignoredFailureCallbackError) {
      }
    },

    failStart: function (sessionId, error) {
      if (sessionId !== CowartWebGLAudioRecorder.activeSessionId) {
        return;
      }

      CowartWebGLAudioRecorder.pending = false;
      CowartWebGLAudioRecorder.recording = false;
      CowartWebGLAudioRecorder.finalizing = false;
      CowartWebGLAudioRecorder.stopRequested = false;
      CowartWebGLAudioRecorder.resetPcmPages();
      CowartWebGLAudioRecorder.streamPcmBuffer = new Uint8Array(0);
      CowartWebGLAudioRecorder.resetResamplerState();
      CowartWebGLAudioRecorder.maximumOutputFrames = 0;
      CowartWebGLAudioRecorder.capturedOutputFrameCount = 0;
      CowartWebGLAudioRecorder.closeCapture();
      CowartWebGLAudioRecorder.activeSessionId = 0;
      CowartWebGLAudioRecorder.sessionId++;
      CowartWebGLAudioRecorder.notifyState(1, 'Browser microphone access failed: ' + String(error));
    }
  },

  CowartWebGLAudio_RegisterCallbacks__deps: ['$CowartWebGLAudioRecorder'],
  CowartWebGLAudio_RegisterCallbacks: function (stateCallback, dataCallback, levelCallback, streamCallback) {
    CowartWebGLAudioRecorder.stateCallback = stateCallback;
    CowartWebGLAudioRecorder.dataCallback = dataCallback;
    CowartWebGLAudioRecorder.levelCallback = levelCallback;
    CowartWebGLAudioRecorder.streamCallback = streamCallback;
  },

  CowartWebGLAudio_UnregisterCallbacks__deps: ['$CowartWebGLAudioRecorder'],
  CowartWebGLAudio_UnregisterCallbacks: function () {
    CowartWebGLAudioRecorder.abortCapture();
    CowartWebGLAudioRecorder.clearLastRecording();
    CowartWebGLAudioRecorder.stateCallback = 0;
    CowartWebGLAudioRecorder.dataCallback = 0;
    CowartWebGLAudioRecorder.levelCallback = 0;
    CowartWebGLAudioRecorder.streamCallback = 0;
  },

  CowartWebGLAudio_IsSupported__deps: ['$CowartWebGLAudioRecorder'],
  CowartWebGLAudio_IsSupported: function () {
    var AudioContextClass = window.AudioContext || window.webkitAudioContext;
    return window.isSecureContext &&
      navigator.mediaDevices &&
      navigator.mediaDevices.getUserMedia &&
      AudioContextClass ? 1 : 0;
  },

  CowartWebGLAudio_Start__deps: ['$CowartWebGLAudioRecorder'],
  CowartWebGLAudio_Start: function (sampleRate, maxDurationMilliseconds, streamChunkMilliseconds) {
    if (CowartWebGLAudioRecorder.pending ||
        CowartWebGLAudioRecorder.recording ||
        CowartWebGLAudioRecorder.finalizing ||
        CowartWebGLAudioRecorder.activeSessionId !== 0 ||
        !CowartWebGLAudioRecorder.stateCallback ||
        !CowartWebGLAudioRecorder.dataCallback) {
      return 0;
    }

    var AudioContextClass = window.AudioContext || window.webkitAudioContext;
    if (!window.isSecureContext ||
        !navigator.mediaDevices ||
        !navigator.mediaDevices.getUserMedia ||
        !AudioContextClass) {
      return 0;
    }

    CowartWebGLAudioRecorder.pending = true;
    CowartWebGLAudioRecorder.stopRequested = false;
    CowartWebGLAudioRecorder.targetSampleRate = Math.max(8000, Math.min(48000, sampleRate));
    CowartWebGLAudioRecorder.streamChunkMilliseconds = Math.max(
      0,
      Math.min(1000, streamChunkMilliseconds)
    );
    CowartWebGLAudioRecorder.maximumOutputFrames = Math.max(
      1,
      Math.floor(
        Math.max(0, maxDurationMilliseconds) *
        CowartWebGLAudioRecorder.targetSampleRate /
        1000
      )
    );
    CowartWebGLAudioRecorder.capturedOutputFrameCount = 0;
    CowartWebGLAudioRecorder.streamPcmBuffer = new Uint8Array(0);
    CowartWebGLAudioRecorder.streamSequence = 0;
    CowartWebGLAudioRecorder.streamOutputFrameCount = 0;
    CowartWebGLAudioRecorder.resetResamplerState();
    CowartWebGLAudioRecorder.resetPcmPages();
    var sessionId = ++CowartWebGLAudioRecorder.sessionId;
    CowartWebGLAudioRecorder.activeSessionId = sessionId;
    CowartWebGLAudioRecorder.finalizing = false;
    var context = null;
    var requestedStream = null;
    var resumePromise = null;
    var mediaPromise = null;

    try {
      context = new AudioContextClass();
      CowartWebGLAudioRecorder.audioContext = context;
      var resumeResult = context.state === 'suspended' ? context.resume() : null;
      resumePromise = Promise.resolve(resumeResult);
      mediaPromise = navigator.mediaDevices.getUserMedia({ audio: true, video: false })
        .then(function (stream) {
          requestedStream = stream;
          if (sessionId !== CowartWebGLAudioRecorder.activeSessionId) {
            CowartWebGLAudioRecorder.stopStreamTracks(stream);
            return null;
          }

          return stream;
        });
    } catch (error) {
      CowartWebGLAudioRecorder.failStart(sessionId, error);
      return 0;
    }

    Promise.all([resumePromise, mediaPromise]).then(function (results) {
      var stream = results[1];
      if (sessionId !== CowartWebGLAudioRecorder.activeSessionId || !stream) {
        CowartWebGLAudioRecorder.stopStreamTracks(stream);
        return;
      }

      try {
        CowartWebGLAudioRecorder.stream = stream;
        CowartWebGLAudioRecorder.inputSampleRate = context.sampleRate;
        CowartWebGLAudioRecorder.sourceNode = context.createMediaStreamSource(stream);
        CowartWebGLAudioRecorder.processorNode = context.createScriptProcessor(2048, 1, 1);
        CowartWebGLAudioRecorder.processorNode.onaudioprocess = function (event) {
          CowartWebGLAudioRecorder.handleAudioProcess(event, sessionId);
        };

        CowartWebGLAudioRecorder.sourceNode.connect(CowartWebGLAudioRecorder.processorNode);
        CowartWebGLAudioRecorder.processorNode.connect(context.destination);
        CowartWebGLAudioRecorder.pending = false;
        CowartWebGLAudioRecorder.recording = true;
        CowartWebGLAudioRecorder.maxTimer = setTimeout(function () {
          CowartWebGLAudioRecorder.finishRecording(sessionId);
        }, Math.max(1000, maxDurationMilliseconds));
        CowartWebGLAudioRecorder.notifyState(0, '');
      } catch (error) {
        CowartWebGLAudioRecorder.failStart(sessionId, error);
      }
    }).catch(function (error) {
      CowartWebGLAudioRecorder.stopStreamTracks(requestedStream);
      CowartWebGLAudioRecorder.failStart(sessionId, error);
    });

    return 1;
  },

  CowartWebGLAudio_Stop__deps: ['$CowartWebGLAudioRecorder'],
  CowartWebGLAudio_Stop: function () {
    if (CowartWebGLAudioRecorder.pending) {
      CowartWebGLAudioRecorder.abortCapture();
      CowartWebGLAudioRecorder.notifyState(2, '');
      return 1;
    }

    return CowartWebGLAudioRecorder.finishRecording(
      CowartWebGLAudioRecorder.activeSessionId
    ) ? 1 : 0;
  },

  CowartWebGLAudio_Play__deps: ['$CowartWebGLAudioRecorder'],
  CowartWebGLAudio_Play: function () {
    if (!CowartWebGLAudioRecorder.playbackElement) {
      return 0;
    }

    try {
      CowartWebGLAudioRecorder.playbackElement.currentTime = 0;
      var playPromise = CowartWebGLAudioRecorder.playbackElement.play();
      if (playPromise && playPromise.catch) {
        playPromise.catch(function (error) {
          console.error('Browser recording playback failed.', error);
        });
      }
      return 1;
    } catch (error) {
      console.error('Browser recording playback failed.', error);
      return 0;
    }
  },

  CowartWebGLAudio_StopPlayback__deps: ['$CowartWebGLAudioRecorder'],
  CowartWebGLAudio_StopPlayback: function () {
    if (CowartWebGLAudioRecorder.playbackElement) {
      CowartWebGLAudioRecorder.playbackElement.pause();
      CowartWebGLAudioRecorder.playbackElement.currentTime = 0;
    }
  },

  CowartWebGLAudio_IsPlaying__deps: ['$CowartWebGLAudioRecorder'],
  CowartWebGLAudio_IsPlaying: function () {
    var element = CowartWebGLAudioRecorder.playbackElement;
    return element && !element.paused && !element.ended ? 1 : 0;
  },

  CowartWebGLAudio_GetPlaybackTimeMilliseconds__deps: ['$CowartWebGLAudioRecorder'],
  CowartWebGLAudio_GetPlaybackTimeMilliseconds: function () {
    var element = CowartWebGLAudioRecorder.playbackElement;
    if (!element || !isFinite(element.currentTime)) {
      return 0;
    }

    return Math.max(0, Math.round(element.currentTime * 1000));
  },

  CowartWebGLAudio_GetPlaybackDurationMilliseconds__deps: ['$CowartWebGLAudioRecorder'],
  CowartWebGLAudio_GetPlaybackDurationMilliseconds: function () {
    var element = CowartWebGLAudioRecorder.playbackElement;
    if (!element || !isFinite(element.duration)) {
      return 0;
    }

    return Math.max(0, Math.round(element.duration * 1000));
  },

  CowartWebGLAudio_Clear__deps: ['$CowartWebGLAudioRecorder'],
  CowartWebGLAudio_Clear: function () {
    CowartWebGLAudioRecorder.abortCapture();
    CowartWebGLAudioRecorder.clearLastRecording();
  }
};

mergeInto(LibraryManager.library, CowartWebGLAudioRecorderLibrary);
