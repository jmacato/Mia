import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import test from 'node:test'

import { downloadTransferFile } from
    '../src/Mia.Browser/wwwroot/file-download.js'

const downloads = []
const blobs = new Map()
let nextUrl = 1

URL.createObjectURL = blob => {
    const url = `blob:test-${nextUrl++}`
    blobs.set(url, blob)
    return url
}
URL.revokeObjectURL = url => blobs.delete(url)

globalThis.document = {
    body: {
        append() {},
    },
    createElement(tag) {
        assert.equal(tag, 'a')
        return {
            hidden: false,
            href: '',
            download: '',
            click() {
                downloads.push({
                    name: this.download,
                    blob: blobs.get(this.href),
                })
            },
            remove() {},
        }
    },
}

function memoryView(bytes) {
    return {
        length: bytes.length,
        copyTo(destination) {
            destination.set(bytes)
        },
    }
}

function sha256(bytes) {
    return createHash('sha256').update(bytes).digest('hex')
}

function payload(length, seed) {
    return Uint8Array.from(
        { length },
        (_, index) => (seed + (index * 37) + (index >>> 2)) & 0xff)
}

const baseGif = Uint8Array.from(Buffer.from(
    'R0lGODlhEAAQAPAAAFpo/wAAACH5BAAAAAAALAAAAAAQABAAAAIOhI+py+0Po5y02ouzPgUAOw==',
    'base64'))
const gif357 = Uint8Array.from([
    ...baseGif.subarray(0, -1),
    0x21, 0xfe, 255,
    ...new Uint8Array(255).fill(0x41),
    42,
    ...new Uint8Array(42).fill(0x42),
    0,
    0x3b,
])

test('IrDA and Bluetooth downloads preserve repeated transaction hashes',
    async () => {
        const transactions = [
            {
                transport: 'IrDA',
                name: 'first.gif',
                type: 'image/gif',
                bytes: gif357,
                hash:
                    'eae776b60800cd2ed99bbdaf9f97ee1626c1fdca3c35c4a68a6f12b8e3af0b20',
            },
            {
                transport: 'IrDA',
                name: 'second.gif',
                type: 'image/gif',
                bytes: payload(613, 0x68),
                hash:
                    'ff7de84cfa0c820ce5d8a2ef2b8e88eec1504c6fa38dd206b6c1c7aaf8e19fea',
            },
            {
                transport: 'Bluetooth',
                name: 'third.gif',
                type: 'image/gif',
                bytes: gif357,
                hash:
                    'eae776b60800cd2ed99bbdaf9f97ee1626c1fdca3c35c4a68a6f12b8e3af0b20',
            },
            {
                transport: 'Bluetooth',
                name: 'fourth.gif',
                type: 'image/gif',
                bytes: payload(613, 0xb4),
                hash:
                    'e6d9c67fa9fde605fe44bd0021a1fde996806cf62ac7ffc5fae2eada0e8bd43c',
            },
        ]

        for (const transaction of transactions) {
            assert.equal(
                sha256(transaction.bytes),
                transaction.hash,
                `${transaction.transport} fixture hash changed`)
            downloadTransferFile(
                transaction.name,
                transaction.type,
                memoryView(transaction.bytes))
        }

        assert.equal(downloads.length, transactions.length)
        for (let index = 0; index < transactions.length; index++) {
            const expected = transactions[index]
            const actual = downloads[index]
            assert.equal(actual.name, expected.name)
            assert.ok(actual.blob instanceof Blob)
            assert.equal(actual.blob.type, expected.type)
            const bytes = new Uint8Array(await actual.blob.arrayBuffer())
            assert.equal(bytes.length, expected.bytes.length)
            assert.equal(sha256(bytes), expected.hash)
            assert.deepEqual(bytes, expected.bytes)
        }
    })
