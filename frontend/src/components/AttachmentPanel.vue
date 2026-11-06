<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, errorMessage, uploadFile } from '@/api/http'
import { formatDateTime } from '@/utils/date'

interface AttachmentRow {
  id: number
  fileName: string
  contentType: string
  size: number
  createdAt: string
}

const props = defineProps<{ ownerType: string; ownerId: number; canUpload: boolean }>()

const rows = ref<AttachmentRow[]>([])
const error = ref('')
const uploading = ref(false)

async function load() {
  try {
    rows.value = await api<AttachmentRow[]>(`/attachments?ownerType=${props.ownerType}&ownerId=${props.ownerId}`)
  } catch (cause) {
    error.value = errorMessage(cause)
  }
}

async function upload(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) return
  const form = new FormData()
  form.set('ownerType', props.ownerType)
  form.set('ownerId', String(props.ownerId))
  form.set('file', file)
  uploading.value = true
  error.value = ''
  try {
    await uploadFile('/attachments', form)
    input.value = ''
    await load()
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    uploading.value = false
  }
}

async function remove(row: AttachmentRow) {
  if (!window.confirm(`فایل «${row.fileName}» حذف شود؟ این کار برگشت ندارد.`)) return
  error.value = ''
  try {
    await api(`/attachments/${row.id}`, { method: 'DELETE' })
    await load()
  } catch (cause) {
    error.value = errorMessage(cause)
  }
}

onMounted(load)
</script>

<template>
  <div>
    <div class="form-label">فایل‌های ضمیمه</div>
    <ul v-if="rows.length" class="list-unstyled small mb-2">
      <li v-for="row in rows" :key="row.id"><a :href="`/api/attachments/${row.id}`" target="_blank" rel="noopener">{{ row.fileName }}</a> — {{ formatDateTime(row.createdAt) }}<button v-if="canUpload" type="button" class="btn btn-soft-danger btn-sm ms-2" :aria-label="`حذف فایل ${row.fileName}`" @click="remove(row)">حذف</button></li>
    </ul>
    <p v-else class="small text-muted mb-2">فایلی ضمیمه نشده است.</p>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <input v-if="canUpload" class="form-control" type="file" accept="application/pdf,image/png,image/jpeg" :disabled="uploading" aria-label="بارگذاری فایل (PDF، PNG یا JPEG تا ۱۰ مگابایت)" @change="upload" />
  </div>
</template>
