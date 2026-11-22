use crate::bytes::{i64_at, u16_at, u32_at, utf16_words};
use std::fmt::Write as _;

const ALIGN: usize = 8;
const MAX_RECORD: usize = 64 * 1024;

pub struct NameFields {
    pub text: String,
    pub escaped: bool,
    pub stamp: i64,
    pub security: u32,
    pub attr: u32,
}
pub struct Ranges<'a> {
    pub remaining: u32,
    pub stride: usize,
    bytes: &'a [u8],
}
impl Ranges<'_> {
    pub fn entries(&self) -> impl Iterator<Item = (i64, i64)> + '_ {
        self.bytes
            .chunks_exact(self.stride)
            .map(|e| (i64_at(e, 0), i64_at(e, 8)))
    }
}
pub enum Fields<'a> {
    Name(NameFields),
    Range(Ranges<'a>),
}
pub struct Record<'a> {
    pub size: usize,
    pub version: u16,
    pub minor: u16,
    pub file: &'a [u8],
    pub parent: &'a [u8],
    pub usn: i64,
    pub reason: u32,
    pub source: u32,
    pub fields: Fields<'a>,
}
pub enum Step<'a> {
    Record(Record<'a>),
    Zero(usize),
    Unsupported(usize, u16),
    Damaged(usize),
    More,
}

/// A dump may start at any byte alignment. Require a whole, plausible named
/// USN record. A lone V4 range has too few independent fields for this mode.
pub fn next_carved(data: &[u8], eof: bool) -> Step<'_> {
    let mut i = 0;
    while i + 8 <= data.len() {
        let header = &data[i..];
        if matches!(header[4], 2 | 3) && header[5..8] == [0; 3] {
            let size = u32_at(header, 0) as usize;
            if (64..=1024).contains(&size) && size.is_multiple_of(ALIGN) {
                if header.len() < size && !eof {
                    return if i == 0 { Step::More } else { Step::Zero(i) };
                }
                if let Step::Record(r) = next(header, true, true)
                    && let Fields::Name(n) = &r.fields
                    && r.usn > 0
                    && r.reason != 0
                    && n.stamp > 0
                    && r.file.iter().any(|&x| x != 0)
                    && r.parent.iter().any(|&x| x != 0)
                    && !n.text.contains('\0')
                {
                    return if i == 0 {
                        Step::Record(r)
                    } else {
                        Step::Zero(i)
                    };
                }
            }
        }
        i += 1;
    }
    if eof {
        Step::Zero(data.len())
    } else if i == 0 {
        Step::More
    } else {
        Step::Zero(i)
    }
}

fn name_fields(record: &[u8], t: usize) -> Option<NameFields> {
    let header = t + 36;
    if record.len() < header {
        return None;
    }
    let length = usize::from(u16_at(record, header - 4));
    let start = usize::from(u16_at(record, header - 2));
    if start < header || !(start | length).is_multiple_of(2) || !(2..=510).contains(&length) {
        return None;
    }
    let bytes = record.get(start..start + length)?;
    if u16_at(record, 6) == 0 && record.len() - (start + length) >= ALIGN {
        return None;
    }
    let mut text = String::with_capacity(length);
    let mut escaped = false;
    for ch in char::decode_utf16(utf16_words(bytes)) {
        match ch {
            Ok(ch) => text.push(ch),
            Err(invalid) => {
                escaped = true;
                write!(text, "\\u{{{:04X}}}", invalid.unpaired_surrogate()).unwrap();
            }
        }
    }
    Some(NameFields {
        text,
        escaped,
        stamp: i64_at(record, t + 8),
        security: u32_at(record, t + 24),
        attr: u32_at(record, t + 28),
    })
}
fn ranges(record: &[u8]) -> Option<Ranges<'_>> {
    if record.len() < 64 {
        return None;
    }
    let count = usize::from(u16_at(record, 60));
    let stride = usize::from(u16_at(record, 62));
    let end = 64usize.checked_add(count.checked_mul(stride)?)?;
    if stride < 16 || record.len() < end || record.len() - end >= ALIGN {
        return None;
    }
    Some(Ranges {
        remaining: u32_at(record, 56),
        stride,
        bytes: &record[64..end],
    })
}

/// The input starts at an aligned offset. While regaining alignment we trust only
/// a complete known structure, never a length claimed by an unknown version.
pub fn next(data: &[u8], eof: bool, seeking: bool) -> Step<'_> {
    if data.len() < ALIGN {
        return if !eof {
            Step::More
        } else if data.iter().all(|&b| b == 0) {
            Step::Zero(data.len())
        } else {
            Step::Damaged(data.len())
        };
    }
    if data[..ALIGN] == [0; ALIGN] {
        let (blocks, _) = data.as_chunks::<ALIGN>();
        let size = blocks.iter().take_while(|b| **b == [0; ALIGN]).count() * ALIGN;
        return Step::Zero(size);
    }
    let size = u32_at(data, 0) as usize;
    let version = u16_at(data, 4);
    let min = match version {
        2 => 64,
        3 => 80,
        4 => 64,
        5.. if !seeking => ALIGN,
        _ => return Step::Damaged(ALIGN),
    };
    if !(min..=MAX_RECORD).contains(&size) || !size.is_multiple_of(ALIGN) {
        return Step::Damaged(ALIGN);
    }
    let Some(record) = data.get(..size) else {
        return if eof {
            Step::Damaged(ALIGN)
        } else {
            Step::More
        };
    };
    if version > 4 {
        return Step::Unsupported(size, version);
    }
    let width = if version == 2 { 8 } else { 16 };
    let t = 8 + 2 * width;
    let fields = if version == 4 {
        ranges(record).map(Fields::Range)
    } else {
        name_fields(record, t).map(Fields::Name)
    };
    let Some(fields) = fields else {
        return Step::Damaged(ALIGN);
    };
    let flags = if version == 4 { 48 } else { t + 16 };
    Step::Record(Record {
        size,
        version,
        minor: u16_at(record, 6),
        file: &record[8..8 + width],
        parent: &record[8 + width..t],
        usn: i64_at(record, t),
        reason: u32_at(record, flags),
        source: u32_at(record, flags + 4),
        fields,
    })
}
