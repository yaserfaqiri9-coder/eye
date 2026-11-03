<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useAuthStore } from '@/stores/auth'

interface DeskRoom {
  desk: string
  sectionLabel: string
  room: number | null
}

interface DoctorRoom {
  staffId: number
  name: string
  hasAccount: boolean
  room: number | null
}

const auth = useAuthStore()
const maxRoom = ref(20)
const desks = ref<DeskRoom[]>([])
const doctors = ref<DoctorRoom[]>([])
const loading = ref(true)
const saving = ref(false)
const error = ref('')
const notice = ref('')

// An emptied number input binds an empty string, which means "no room" here.
const room = (value: number | string | null) => (value === '' || value === null ? null : Number(value))

async function save() {
  saving.value = true
  error.value = ''
  notice.value = ''
  try {
    await api('/calls/settings', {
      method: 'PUT',
      body: {
        desks: desks.value.map((d) => ({ desk: d.desk, room: room(d.room) })),
        doctors: doctors.value.map((d) => ({ staffId: d.staffId, room: room(d.room) })),
      },
    })
    notice.value = 'ذخیره شد.'
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    saving.value = false
  }
}

onMounted(async () => {
  try {
    const settings = await api<{ maxRoom: number; desks: DeskRoom[]; doctors: DoctorRoom[] }>('/calls/settings')
    maxRoom.value = settings.maxRoom
    desks.value = settings.desks
    doctors.value = settings.doctors
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
  <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>
  <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
  <form v-else @submit.prevent="save">
    <fieldset :disabled="!auth.can('settings.edit')">
      <p class="form-hint">اگر برای بخشی اتاق تعیین نشود، نام همان بخش اعلان می‌شود. مجموعه صوتی اتاق‌های ۱ تا {{ maxRoom }} را دارد.</p>
      <div class="xr-table-wrap mb-3">
        <table class="xr-table">
          <thead><tr><th>بخش</th><th>شماره اتاق</th></tr></thead>
          <tbody>
            <tr v-for="row in desks" :key="row.desk">
              <td>{{ row.sectionLabel }}</td>
              <td><input v-model="row.room" class="form-control form-control-sm" type="number" min="1" :max="maxRoom" :aria-label="`اتاق ${row.sectionLabel}`" /></td>
            </tr>
          </tbody>
        </table>
      </div>

      <div class="xr-table-wrap mb-3">
        <table class="xr-table">
          <thead><tr><th>داکتر</th><th>شماره اتاق</th></tr></thead>
          <tbody>
            <tr v-if="doctors.length === 0"><td colspan="2" class="text-muted">داکتر فعالی ثبت نشده است.</td></tr>
            <tr v-for="row in doctors" :key="row.staffId">
              <td>{{ row.name }} <span v-if="!row.hasAccount" class="xr-badge xr-badge--orange">بدون حساب کاربری</span></td>
              <td><input v-model="row.room" class="form-control form-control-sm" type="number" min="1" :max="maxRoom" :aria-label="`اتاق ${row.name}`" /></td>
            </tr>
          </tbody>
        </table>
      </div>
      <button type="submit" class="btn btn-primary" :disabled="saving">ذخیره</button>
    </fieldset>
  </form>
</template>
