<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { api, errorMessage, uploadFile } from '@/api/http'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import type { ServiceRow } from '@/features/finance/types'
import { useAuthStore } from '@/stores/auth'
import { money } from '@/utils/money'

interface TestRow {
  id: number
  name: string
  isActive: boolean
}

const auth = useAuthStore()
const tab = ref('hospital')
const error = ref('')
const notice = ref('')
const services = ref<ServiceRow[]>([])
const tests = ref<TestRow[]>([])
const registers = ref<{ id: number; name: string }[]>([])
const logoId = ref<number | null>(null)

const tabs = [
  { key: 'hospital', label: 'مشخصات شفاخانه' },
  { key: 'services', label: 'خدمات و تعرفه‌ها' },
  { key: 'tests', label: 'فهرست آزمایش‌ها' },
  { key: 'registers', label: 'صندوق‌ها' },
]

const canEdit = () => auth.can('settings.edit')

const hospital = reactive({ hospitalName: '', address: '', phone: '', printFooter: '', saving: false })

function say(message: string) {
  notice.value = message
  error.value = ''
}

function fail(cause: unknown) {
  error.value = errorMessage(cause)
  notice.value = ''
}

async function load() {
  try {
    const settings = await api<typeof hospital & { logoAttachmentId: number | null }>('/settings')
    Object.assign(hospital, { hospitalName: settings.hospitalName ?? '', address: settings.address ?? '', phone: settings.phone ?? '', printFooter: settings.printFooter ?? '' })
    logoId.value = settings.logoAttachmentId
    services.value = await api<ServiceRow[]>('/services')
    if (auth.can('diagnostics.view')) tests.value = await api<TestRow[]>('/diagnostic-tests')
    if (auth.can('cash.view')) registers.value = await api<typeof registers.value>('/cash/registers')
  } catch (cause) {
    fail(cause)
  }
}

async function saveHospital() {
  hospital.saving = true
  try {
    await api('/settings', { method: 'PUT', body: { hospitalName: hospital.hospitalName, address: hospital.address, phone: hospital.phone, printFooter: hospital.printFooter } })
    say('ذخیره شد.')
  } catch (cause) {
    fail(cause)
  } finally {
    hospital.saving = false
  }
}

async function uploadLogo(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0]
  if (!file) return
  const form = new FormData()
  form.set('ownerType', 'Logo')
  form.set('ownerId', '0')
  form.set('file', file)
  try {
    logoId.value = (await uploadFile<{ id: number }>('/attachments', form)).id
    say('لوگو ذخیره شد.')
  } catch (cause) {
    fail(cause)
  }
}

const service = reactive({ open: false, code: '', name: '', price: 0, saving: false, error: '' })

async function saveService() {
  service.saving = true
  service.error = ''
  try {
    await api('/services', { method: 'POST', body: { code: service.code, name: service.name, isActive: true, price: service.price } })
    service.open = false
    services.value = await api<ServiceRow[]>('/services')
  } catch (cause) {
    service.error = errorMessage(cause)
  } finally {
    service.saving = false
  }
}

const price = reactive({ row: null as ServiceRow | null, value: 0, saving: false, error: '' })

async function savePrice() {
  price.saving = true
  price.error = ''
  try {
    await api(`/services/${price.row!.id}/price`, { method: 'POST', body: { price: price.value } })
    price.row = null
    services.value = await api<ServiceRow[]>('/services')
  } catch (cause) {
    price.error = errorMessage(cause)
  } finally {
    price.saving = false
  }
}

const test = reactive({ open: false, name: '', serviceId: null as number | null, saving: false, error: '' })

async function saveTest() {
  test.saving = true
  test.error = ''
  try {
    await api('/diagnostic-tests', { method: 'POST', body: { name: test.name, isActive: true, serviceId: test.serviceId } })
    test.open = false
    tests.value = await api<TestRow[]>('/diagnostic-tests')
  } catch (cause) {
    test.error = errorMessage(cause)
  } finally {
    test.saving = false
  }
}

const registerName = ref('')

async function saveRegister() {
  try {
    await api('/cash/registers', { method: 'POST', body: { name: registerName.value } })
    registerName.value = ''
    registers.value = await api<typeof registers.value>('/cash/registers')
  } catch (cause) {
    fail(cause)
  }
}

onMounted(load)
</script>

<template>
  <div v-if="error" class="alert alert-danger py-2" role="alert">{{ error }}</div>
  <div v-if="notice" class="alert alert-success py-2" role="status">{{ notice }}</div>

  <section class="xr-card">
    <div class="xr-card__header flex-wrap">
      <nav class="xr-tabs" role="tablist" aria-label="بخش‌های تنظیمات">
        <button v-for="t in tabs" :key="t.key" type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === t.key }" :aria-selected="tab === t.key" @click="tab = t.key">{{ t.label }}</button>
      </nav>
    </div>
    <div class="xr-card__body">
      <form v-if="tab === 'hospital'" @submit.prevent="saveHospital">
        <fieldset :disabled="!canEdit()">
          <div class="row g-3 mb-3">
            <div class="col-md-6"><label class="form-label" for="h-name">نام شفاخانه</label><input id="h-name" v-model="hospital.hospitalName" class="form-control" maxlength="150" /></div>
            <div class="col-md-6"><label class="form-label" for="h-phone">تماس</label><input id="h-phone" v-model="hospital.phone" class="form-control" dir="ltr" maxlength="60" /></div>
            <div class="col-12"><label class="form-label" for="h-address">آدرس</label><input id="h-address" v-model="hospital.address" class="form-control" maxlength="300" /></div>
            <div class="col-12"><label class="form-label" for="h-footer">پاورقی چاپ</label><input id="h-footer" v-model="hospital.printFooter" class="form-control" maxlength="300" /></div>
            <div class="col-12">
              <label class="form-label" for="h-logo">لوگو (PNG یا JPEG)</label>
              <input id="h-logo" class="form-control" type="file" accept="image/png,image/jpeg" @change="uploadLogo" />
              <img v-if="logoId" :src="`/api/attachments/${logoId}`" alt="لوگوی فعلی" class="mt-2" style="max-height: 60px" />
            </div>
          </div>
          <button type="submit" class="btn btn-primary" :disabled="hospital.saving">ذخیره</button>
        </fieldset>
      </form>

      <template v-else-if="tab === 'services'">
        <button v-if="canEdit()" type="button" class="btn btn-primary mb-3" @click="Object.assign(service, { open: true, code: '', name: '', price: 0, error: '' })">خدمت جدید</button>
        <p v-if="services.length === 0" class="text-muted">خدمتی تعریف نشده است.</p>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>کد</th><th>نام</th><th>قیمت فعلی</th><th v-if="canEdit()">عملیات</th></tr></thead>
            <tbody>
              <tr v-for="row in services" :key="row.id">
                <td><span class="ltr-num">{{ row.code }}</span></td>
                <td>{{ row.name }}</td>
                <td>{{ money(row.currentPrice) }}</td>
                <td v-if="canEdit()"><button type="button" class="btn btn-soft btn-sm" @click="Object.assign(price, { row, value: row.currentPrice ?? 0, error: '' })">تغییر قیمت</button></td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>

      <template v-else-if="tab === 'tests'">
        <button v-if="canEdit()" type="button" class="btn btn-primary mb-3" @click="Object.assign(test, { open: true, name: '', serviceId: null, error: '' })">آزمایش جدید</button>
        <p v-if="tests.length === 0" class="text-muted">آزمایشی تعریف نشده یا اجازه مشاهده ندارید.</p>
        <ul v-else class="mb-0"><li v-for="row in tests" :key="row.id">{{ row.name }}</li></ul>
      </template>

      <template v-else>
        <form v-if="canEdit()" class="row g-2 mb-3" @submit.prevent="saveRegister">
          <div class="col-md-5"><input v-model="registerName" class="form-control" placeholder="نام صندوق جدید" aria-label="نام صندوق جدید" maxlength="100" required /></div>
          <div class="col-auto"><button type="submit" class="btn btn-primary">افزودن صندوق</button></div>
        </form>
        <p v-if="registers.length === 0" class="text-muted">صندوقی نمایش داده نمی‌شود یا اجازه مشاهده ندارید.</p>
        <ul v-else class="mb-0"><li v-for="row in registers" :key="row.id">{{ row.name }}</li></ul>
      </template>
    </div>
  </section>

  <UiModal v-if="service.open" title="خدمت جدید" @close="service.open = false">
    <form @submit.prevent="saveService">
      <div class="mb-3"><label class="form-label" for="sv-code">کد</label><input id="sv-code" v-model="service.code" class="form-control" dir="ltr" maxlength="30" required /></div>
      <div class="mb-3"><label class="form-label" for="sv-name">نام خدمت</label><input id="sv-name" v-model="service.name" class="form-control" maxlength="150" required /></div>
      <div class="mb-3"><label class="form-label" for="sv-price">قیمت</label><input id="sv-price" v-model.number="service.price" class="form-control" type="number" min="0" step="0.01" required /></div>
      <div v-if="service.error" class="alert alert-danger py-2" role="alert">{{ service.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="service.saving">ذخیره</button>
    </form>
  </UiModal>

  <UiModal v-if="price.row" :title="`قیمت ${price.row.name}`" @close="price.row = null">
    <form @submit.prevent="savePrice">
      <p class="small text-muted">قیمت جدید فقط روی فاکتورهای بعدی اثر دارد.</p>
      <label class="form-label" for="pr-value">قیمت جدید</label>
      <input id="pr-value" v-model.number="price.value" class="form-control mb-3" type="number" min="0" step="0.01" required />
      <div v-if="price.error" class="alert alert-danger py-2" role="alert">{{ price.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="price.saving">ذخیره</button>
    </form>
  </UiModal>

  <UiModal v-if="test.open" title="آزمایش جدید" @close="test.open = false">
    <form @submit.prevent="saveTest">
      <div class="mb-3"><label class="form-label" for="ts-name">نام آزمایش</label><input id="ts-name" v-model="test.name" class="form-control" maxlength="150" required /></div>
      <div class="mb-3">
        <label class="form-label" for="ts-service">خدمت قابل پرداخت</label>
        <select id="ts-service" v-model.number="test.serviceId" class="form-select">
          <option :value="null">بدون هزینه خودکار</option>
          <option v-for="s in services" :key="s.id" :value="s.id">{{ s.name }}</option>
        </select>
      </div>
      <div v-if="test.error" class="alert alert-danger py-2" role="alert">{{ test.error }}</div>
      <button type="submit" class="btn btn-primary" :disabled="test.saving">ذخیره</button>
    </form>
  </UiModal>
</template>
