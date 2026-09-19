interface QuitEvent {
  preventDefault(): void;
}

export class AppShutdown {
  #started = false;
  #ready = false;

  constructor(
    private readonly cleanup: () => Promise<void>,
    private readonly quit: () => void,
    private readonly reportError: (error: unknown) => void,
    private readonly timeoutMs = 6_000,
  ) {}

  get isShuttingDown(): boolean { return this.#started; }

  beforeQuit(event: QuitEvent): void {
    if (this.#ready) return;
    event.preventDefault();
    if (this.#started) return;
    this.#started = true;
    void this.#finish();
  }

  async #finish(): Promise<void> {
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      await Promise.race([
        Promise.resolve().then(this.cleanup),
        new Promise<never>((_resolve, reject) => {
          timer = setTimeout(() => reject(new Error('Application shutdown cleanup timed out.')), this.timeoutMs);
        }),
      ]);
    } catch (error) {
      this.reportError(error);
    } finally {
      clearTimeout(timer);
      this.#ready = true;
      this.quit();
    }
  }
}
