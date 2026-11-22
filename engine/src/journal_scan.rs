use crate::{
    Result,
    decode::{self, Step},
};
use std::io::Read;

const BLOCK: usize = 1024 * 1024;

pub fn scan(
    mut source: impl Read,
    carving: bool,
    mut on_bytes: impl FnMut(&[u8]),
    mut on_step: impl FnMut(u64, bool, Step<'_>) -> Result<()>,
    mut on_read: impl FnMut(u64, u64),
) -> Result<(u64, u64)> {
    let mut buffer = vec![0; BLOCK];
    let mut pending = Vec::with_capacity(BLOCK + 65536);
    let mut base = 0u64;
    let mut count = 0u64;
    let mut seeking = false;
    loop {
        let read = source.read(&mut buffer)?;
        let bytes = &buffer[..read];
        on_bytes(bytes);
        pending.extend_from_slice(bytes);
        let mut i = 0;
        while i < pending.len() {
            let step = if carving {
                decode::next_carved(&pending[i..], read == 0)
            } else {
                decode::next(&pending[i..], read == 0, seeking)
            };
            let previous_seeking = seeking;
            let size = match &step {
                Step::More => break,
                Step::Record(record) => {
                    seeking = false;
                    count += 1;
                    record.size
                }
                Step::Damaged(size) => {
                    seeking = true;
                    *size
                }
                Step::Zero(size) | Step::Unsupported(size, _) => *size,
            };
            on_step(base + i as u64, previous_seeking, step)?;
            i += size;
        }
        pending.drain(..i);
        base += i as u64;
        if read == 0 {
            return Ok((base, count));
        }
        on_read(base, count);
    }
}
