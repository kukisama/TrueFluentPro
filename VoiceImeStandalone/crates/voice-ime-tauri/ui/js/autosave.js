// A single writer per form. Edits made while a write is pending are a new revision.
export function createAutosave(write, failed) {
  let revision = 0;
  let committed = 0;
  let timer;
  let pending;

  function flush() {
    clearTimeout(timer);
    if (!pending && committed !== revision) {
      pending = (async () => {
        while (committed !== revision) {
          const writing = revision;
          await write(() => writing === revision);
          committed = writing;
        }
      })().finally(() => { pending = undefined; });
    }
    return pending ?? Promise.resolve();
  }

  return {
    get dirty() { return committed !== revision; },
    edited() {
      revision++;
      clearTimeout(timer);
      timer = setTimeout(() => flush().catch(failed), 200);
    },
    flush,
  };
}