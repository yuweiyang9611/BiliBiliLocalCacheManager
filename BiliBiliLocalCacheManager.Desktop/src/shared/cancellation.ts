// A cancellation is normally a confirmed, uneventful outcome. The only cancellation
// that carries actionable detail is an export whose staging directory the Host could
// not roll back: DesktopHostApplication.Media.cs throws
//   RpcException("cancelled", $"导出已取消。{warning}")
// where warning is one of the two RollbackStaging() messages below, ending with the
// location to inspect. Anything else, including Electron-prefixed or arbitrary text,
// is treated as a plain cancellation and never persisted or elevated.
const exportRollbackWarningPattern =
  /^导出已取消。(?:无法确认暂存目录的身份，已跳过清理。请核对：|导出暂存目录未能完全清理，请核对：)\S/u;

export function isCancellationCode(code: unknown): boolean {
  return code === 'cancelled' || code === 'CANCELLED';
}

/** Returns the Host's export rollback warning when the message is exactly in that format. */
export function exportRollbackWarning(message: unknown): string | undefined {
  if (typeof message !== 'string') return undefined;
  const trimmed = message.trim();
  return exportRollbackWarningPattern.test(trimmed) ? trimmed : undefined;
}

/** The actionable warning of a coded cancellation error, or undefined for a plain cancellation. */
export function cancellationWarning(error: unknown): string | undefined {
  if (!(error instanceof Error) || !('code' in error) || !isCancellationCode(error.code)) return undefined;
  return exportRollbackWarning(error.message);
}
