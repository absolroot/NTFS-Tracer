use crate::{
    Result,
    decode::{Fields, Record, Step},
    journal_scan,
};
use std::{
    borrow::Cow,
    collections::HashMap,
    fs::{self, File},
    io::{self, BufWriter, Write},
    path::Path,
};
use time::OffsetDateTime;

const HEADER: &str = "Name,Extension,EntryNumber,SequenceNumber,ParentEntryNumber,ParentSequenceNumber,ParentPath,UpdateSequenceNumber,UpdateTimestamp,UpdateReasons,FileAttributes,OffsetToData,SourceFile";
const FILETIME_UNIX_EPOCH: i64 = 116_444_736_000_000_000;
const ENTRY_MASK: u64 = (1 << 48) - 1;

// The CSV uses MFTECmd's spellings. Unknown flag combinations are decimal.
const REASONS: &[(u32, &str)] = &[
    (0x1, "DataOverwrite"),
    (0x2, "DataExtend"),
    (0x4, "DataTruncation"),
    (0x10, "NamedDataOverwrite"),
    (0x20, "NamedDataExtend"),
    (0x40, "NamedDataTruncation"),
    (0x100, "FileCreate"),
    (0x200, "FileDelete"),
    (0x400, "EaChange"),
    (0x800, "SecurityChange"),
    (0x1000, "RenameOldName"),
    (0x2000, "RenameNewName"),
    (0x4000, "IndexableChange"),
    (0x8000, "BasicInfoChange"),
    (0x10000, "HardLinkChange"),
    (0x20000, "CompressionChange"),
    (0x40000, "EncryptionChange"),
    (0x80000, "ObjectIdChange"),
    (0x100000, "ReparsePointChange"),
    (0x200000, "StreamChange"),
    (0x400000, "TransactedChange"),
    (0x800000, "IntegrityChange"),
    (0x80000000, "Close"),
];
const ATTRIBUTES: &[(u32, &str)] = &[
    (0x1, "ReadOnly"),
    (0x2, "Hidden"),
    (0x4, "System"),
    (0x8, "VolumeLabel"),
    (0x10, "Directory"),
    (0x20, "Archive"),
    (0x40, "Device"),
    (0x80, "Normal"),
    (0x100, "Temporary"),
    (0x200, "SparseFile"),
    (0x400, "ReparsePoint"),
    (0x800, "Compressed"),
    (0x1000, "Offline"),
    (0x2000, "NotContentIndexed"),
    (0x4000, "Encrypted"),
    (0x8000, "IntegrityStream"),
    (0x10000, "Virtual"),
    (0x20000, "NoScrubData"),
    (0x40000, "HasEa"),
    (0x10000000, "IsDirectory"),
    (0x20000000, "IsIndexView"),
];

pub fn export(input: &Path, output: &Path) -> Result<()> {
    let input = std::path::absolute(input)?;
    let output = std::path::absolute(output)?;
    match fs::symlink_metadata(&output) {
        Ok(_) => {
            return Err(
                io::Error::new(io::ErrorKind::AlreadyExists, "CSV output already exists").into(),
            );
        }
        Err(error) if error.kind() == io::ErrorKind::NotFound => {}
        Err(error) => return Err(error.into()),
    }
    let source = File::open(&input)?;
    let folder = output.parent().ok_or_else(|| {
        io::Error::new(io::ErrorKind::InvalidInput, "CSV requires an output file")
    })?;
    let mut temporary = tempfile::Builder::new()
        .prefix(".ntfstracer-")
        .suffix(".partial")
        .tempfile_in(folder)?;
    let mut damaged_bytes = 0;
    let (_, count) = {
        let mut csv = JournalCsv::new(
            BufWriter::with_capacity(128 * 1024, temporary.as_file_mut()),
            &input.to_string_lossy(),
        )?;
        let result = journal_scan::scan(
            source,
            false,
            |_| {},
            |pos, _, step| {
                match step {
                    Step::Record(record) if record.version == 2 => csv.write_record(pos, record)?,
                    Step::Record(record) => return Err(unsupported(record.version, pos).into()),
                    Step::Unsupported(_, version) => return Err(unsupported(version, pos).into()),
                    Step::Damaged(size) => damaged_bytes += size as u64,
                    Step::Zero(_) => {}
                    Step::More => unreachable!("scan only emits completed steps"),
                }
                Ok(())
            },
            |_, _| {},
        )?;
        if result.1 == 0 && damaged_bytes != 0 {
            return Err(
                io::Error::new(io::ErrorKind::InvalidData, "No readable USN V2 records").into(),
            );
        }
        csv.writer.flush()?;
        result
    };
    temporary.into_temp_path().persist_noclobber(&output)?;
    if damaged_bytes > 0 {
        eprintln!("Skipped {damaged_bytes} damaged source bytes.");
    }
    println!("done {count}");
    Ok(())
}

fn unsupported(version: u16, position: u64) -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidData,
        format!("13-column CSV supports USN V2; found V{version} at offset {position}."),
    )
}

struct JournalCsv<W> {
    writer: W,
    source_field: Vec<u8>,
    reasons: HashMap<u32, String>,
    attributes: HashMap<u32, String>,
}

impl<W: Write> JournalCsv<W> {
    fn new(mut writer: W, source: &str) -> io::Result<Self> {
        write!(writer, "\u{feff}{HEADER}\r\n")?;
        let mut source_field = Vec::new();
        write_field(&mut source_field, source)?;
        source_field.extend_from_slice(b"\r\n");
        Ok(Self {
            writer,
            source_field,
            reasons: HashMap::new(),
            attributes: HashMap::new(),
        })
    }

    fn write_record(&mut self, pos: u64, record: Record<'_>) -> io::Result<()> {
        let Fields::Name(name) = record.fields else {
            unreachable!("a V2 record contains name fields");
        };
        let file = u64::from_le_bytes(record.file.try_into().expect("a V2 reference is 8 bytes"));
        let parent =
            u64::from_le_bytes(record.parent.try_into().expect("a V2 reference is 8 bytes"));
        let writer = &mut self.writer;
        write_field(writer, &name.text)?;
        writer.write_all(b",")?;
        write_field(writer, extension(&name.text))?;
        write!(
            writer,
            ",{},{},{},{},,{},",
            file & ENTRY_MASK,
            file >> 48,
            parent & ENTRY_MASK,
            parent >> 48,
            record.usn
        )?;
        write_timestamp(writer, name.stamp)?;
        write!(
            writer,
            ",{},{},{pos},",
            flag_label(&mut self.reasons, record.reason, REASONS, u32::to_string),
            flag_label(&mut self.attributes, name.attr, ATTRIBUTES, |value| (*value
                as i32)
                .to_string())
        )?;
        writer.write_all(&self.source_field)
    }
}

fn extension(name: &str) -> &str {
    let leaf = name.rsplit(['/', '\\']).next().unwrap_or(name);
    match leaf.rfind('.') {
        Some(dot) if dot + 1 < leaf.len() => &leaf[dot..],
        _ => "",
    }
}

fn write_field(writer: &mut impl Write, field: &str) -> io::Result<()> {
    if !field.starts_with(' ') && !field.ends_with(' ') && !field.contains([',', '"', '\r', '\n']) {
        return writer.write_all(field.as_bytes());
    }
    writer.write_all(b"\"")?;
    let mut remaining = field;
    while let Some((before, after)) = remaining.split_once('"') {
        writer.write_all(before.as_bytes())?;
        writer.write_all(b"\"\"")?;
        remaining = after;
    }
    writer.write_all(remaining.as_bytes())?;
    writer.write_all(b"\"")
}

fn write_timestamp(writer: &mut impl Write, stamp: i64) -> io::Result<()> {
    if stamp >= 0
        && let Ok(timestamp) = OffsetDateTime::from_unix_timestamp_nanos(
            (i128::from(stamp) - i128::from(FILETIME_UNIX_EPOCH)) * 100,
        )
    {
        let (year, month, day) = timestamp.to_calendar_date();
        let (hour, minute, second, nanos) = timestamp.to_hms_nano();
        let fraction = nanos / 100;
        write!(
            writer,
            "{year:04}-{:02}-{day:02} {hour:02}:{minute:02}:{second:02}.{fraction:07}",
            u8::from(month)
        )?;
    }
    Ok(())
}

fn flag_label<'a>(
    cache: &'a mut HashMap<u32, String>,
    value: u32,
    flags: &[(u32, &str)],
    decimal: impl FnOnce(&u32) -> String,
) -> Cow<'a, str> {
    let format = || {
        let known = flags.iter().fold(0, |mask, (bit, _)| mask | bit);
        if value == 0 || value & !known != 0 {
            return decimal(&value);
        }
        let mut text = String::new();
        for &(bit, label) in flags {
            if value & bit != 0 {
                if !text.is_empty() {
                    text.push('|');
                }
                text.push_str(label);
            }
        }
        text
    };
    if cache.len() < 1024 {
        return Cow::Borrowed(cache.entry(value).or_insert_with(format));
    }
    cache.get(&value).map_or_else(
        || Cow::Owned(format()),
        |label| Cow::Borrowed(label.as_str()),
    )
}
