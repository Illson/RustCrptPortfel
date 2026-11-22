import { BlobReader, ZipReader, type FileEntry } from '@zip.js/zip.js/lib/zip-core-native.js'

const maxZipBytes = 10 * 1024 * 1024
const maxZipEntries = 100

class ArchiveImportError extends Error {}

export async function readOverviewFromZip(file: Blob, maxCsvBytes: number) {
  if (file.size > maxZipBytes) throw new Error('Размер ZIP должен быть не больше 10 МБ.')
  const zip = new ZipReader(new BlobReader(file), { strictness: 'strict' })
  try {
    let overview: FileEntry | undefined
    let count = 0
    for await (const entry of zip.getEntriesGenerator()) {
      if (++count > maxZipEntries) throw new ArchiveImportError('В ZIP больше 100 записей.')
      if (entry.directory || !/(^|[_-])overview\.csv$/i.test(entry.filename.split('/').at(-1) ?? '')) continue
      if (overview) throw new ArchiveImportError('В ZIP несколько файлов обзора. Выберите архив одного портфеля или загрузите нужный CSV отдельно.')
      overview = entry
    }
    if (!overview) throw new ArchiveImportError('В ZIP не найден *_overview.csv с остатками.')
    if (overview.encrypted) throw new ArchiveImportError('CSV с остатками защищён паролем. Загрузите ZIP без пароля или сам CSV.')

    const tooLarge = 'Размер CSV с остатками после распаковки должен быть не больше 1 МБ.'
    if (overview.uncompressedSize > maxCsvBytes) throw new ArchiveImportError(tooLarge)
    const decoder = new TextDecoder('utf-8', { fatal: true })
    const chunks: string[] = []
    let bytes = 0
    function decode(data?: Uint8Array) {
      try { return decoder.decode(data, { stream: data !== undefined }) }
      catch { throw new ArchiveImportError('CSV с остатками должен быть в UTF-8.') }
    }
    // Bound actual output as well as the declared size; do not extract the other entries.
    await overview.getData(new WritableStream<Uint8Array>({
      write(chunk) {
        bytes += chunk.byteLength
        if (bytes > maxCsvBytes) throw new ArchiveImportError(tooLarge)
        chunks.push(decode(chunk))
      },
    }), { checkSignature: true, checkOverlappingEntry: true, useWebWorkers: false })
    chunks.push(decode())
    return { text: chunks.join(''), entryName: overview.filename.split('/').at(-1)! }
  } catch (failure) {
    if (failure instanceof ArchiveImportError) throw failure
    throw new Error('Не удалось прочитать ZIP: архив повреждён или использует неподдерживаемый формат.')
  } finally {
    await zip.close()
  }
}
