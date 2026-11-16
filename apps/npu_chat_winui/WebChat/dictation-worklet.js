class PhoenixRecorder extends AudioWorkletProcessor {
  constructor(options) {
    super();
    this.samples = new Float32Array(sampleRate * 30);
    this.length = 0;
    this.finished = false;
    this.automatic = options.processorOptions?.automatic === true;
    this.voiced = 0;
    this.quiet = 0;
    this.noise = 0.002;
    this.port.onmessage = () => this.finish();
  }

  finish() {
    if (this.finished) return;
    this.finished = true;
    const length = this.automatic && this.voiced < sampleRate * 0.15 ? 0 : this.length;
    const result = this.samples.subarray(0, length);
    this.port.postMessage(result, [result.buffer]);
  }

  process(inputs) {
    if (this.finished) return false;
    const channel = inputs[0][0];
    if (channel) {
      const samples = channel.subarray(0, this.samples.length - this.length);
      this.samples.set(samples, this.length);
      this.length += samples.length;
      if (this.automatic && samples.length) {
        let square = 0;
        for (const sample of samples) square += sample * sample;
        const level = Math.sqrt(square / samples.length);
        if (level > Math.max(0.008, this.noise * 3)) {
          this.voiced += samples.length;
          this.quiet = 0;
        } else {
          this.noise = 0.995 * this.noise + 0.005 * level;
          this.quiet += samples.length;
        }
        if (this.voiced >= sampleRate * 0.15 && this.quiet >= sampleRate * 2.5) this.finish();
      }
      if (this.length === this.samples.length) this.finish();
    }
    return !this.finished;
  }
}

registerProcessor("phoenix-recorder", PhoenixRecorder);
