//! Optional independent L01, Lx01 and compressed Ex01 generator for the disk tests.
use ewf_image::{
    EwfWriter, LogicalEntryMetadata, LogicalWriter, WriteCompression, WriteFormat, WriteOptions,
};
use std::{fs::File, io, path::Path};

fn entry(name: &str) -> LogicalEntryMetadata {
    LogicalEntryMetadata {
        name: name.to_owned(),
        ..Default::default()
    }
}

fn main() -> ewf_image::Result<()> {
    let dir = std::env::args().nth(1).expect("Expected the generated fixture directory");
    let root = Path::new(&dir);
    for (name, format) in [
        ("logical.L01", WriteFormat::Ewf1Logical),
        ("logical.Lx01", WriteFormat::Ewf2Logical),
    ] {
        let mut writer = LogicalWriter::create(root.join(name), WriteOptions { format, ..Default::default() })?;
        let volume = writer.add_directory(1, entry("Volume"))?;
        let extend = writer.add_directory(volume, entry("$Extend"))?;
        for (parent, collected, oracle) in [
            (extend, "$UsnJrnl:$J", "expected-j.bin"),
            (volume, "$MFT", "tsk-mft.bin"),
            (volume, "$LogFile", "expected-l.bin"),
            (volume, "$Bitmap", "tsk-bitmap.bin"),
        ] {
            let mut source = File::open(root.join("Alpha").join(oracle))?;
            writer.add_file(parent, entry(collected), source.metadata()?.len(), &mut source)?;
        }
        writer.finish()?;
    }

    let mut input = File::open(root.join("case-mbr.raw"))?;
    let mut writer = EwfWriter::create(root.join("physical.Ex01"), WriteOptions {
        format: WriteFormat::Ewf2Physical,
        compression: WriteCompression::Zlib,
        ..Default::default()
    })?;
    io::copy(&mut input, &mut writer)?;
    writer.finish()?;
    Ok(())
}

