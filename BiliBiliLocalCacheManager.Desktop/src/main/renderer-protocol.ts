import path from 'node:path';

export const rendererScheme = 'blcm';
export const rendererHost = 'app';
export const packagedRendererUrl = `${rendererScheme}://${rendererHost}/index.html`;

export function resolveRendererFilePath(rendererRootPath: string, requestUrl: string): string | null {
  try {
    const url = new URL(requestUrl);
    if (url.protocol !== `${rendererScheme}:` ||
        url.host !== rendererHost ||
        url.username ||
        url.password ||
        url.port) {
      return null;
    }

    const pathname = decodeURIComponent(url.pathname).replaceAll('\\', '/');
    if (pathname.includes('\0')) return null;
    const relativePath = pathname === '/' ? 'index.html' : `.${pathname}`;
    const rendererRoot = path.resolve(rendererRootPath);
    const filePath = path.resolve(rendererRoot, relativePath);
    const rendererPrefix = `${rendererRoot}${path.sep}`;
    return filePath.startsWith(rendererPrefix) ? filePath : null;
  } catch {
    return null;
  }
}
